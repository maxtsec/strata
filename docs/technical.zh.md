# Strata — 技術文檔

> 相關文檔：[設計文檔（中文）](design.zh.md) ·
> [Design Document (English)](design.md) ·
> [Technical Document (English)](technical.md)

本文檔講 Strata 如何建構、執行、測試與部署。至於**為什麼**是這個形狀，見
[設計文檔](design.zh.md)。

## 1. 技術選型

| 範疇 | 選擇 |
|---|---|
| Runtime | .NET 10（由 `global.json` 鎖定，10.0.4xx feature band） |
| API | ASP.NET Core Web API，controller-based |
| 資料 | EF Core + Azure SQL（本機用 Docker 內的 SQL Server 2022） |
| 身分 | ASP.NET Core Identity（`AddIdentityCore`）+ JWT bearer（HMAC-SHA256） |
| 檔案 | Azure Blob Storage，user-delegation SAS URI |
| 測試 | xUnit、NetArchTest（分層）、Respawn（資料庫重設） |
| CI/CD | GitHub Actions → Azure App Service（OIDC，不存放憑證） |
| 機密 | Azure Key Vault（部署環境）／user-secrets（本機） |

## 2. 專案結構

```
strata/
├── Strata.slnx
├── global.json
├── AGENTS.md / CLAUDE.md      # AI 協作開發的工作約定
├── README.md
├── .github/workflows/deploy.yml
├── docs/
│   ├── design.md / design.zh.md
│   ├── technical.md / technical.zh.md
│   └── adr/                   # 0001–0004
├── src/
│   ├── Strata.Domain/         # entity、ITenantOwned、IOwnable
│   ├── Strata.Application/    # IApplicationDbContext、IFileStorage、ICurrentTenant
│   ├── Strata.Infrastructure/ # AppDbContext、migrations、Blob Storage、Identity user
│   └── Strata.Api/            # controller、DI wiring、tenancy 與 authorization
└── tests/
    ├── Strata.Architecture.Tests/
    └── Strata.Api.IntegrationTests/
```

Namespace 跟隨資料夾：`Strata.Domain.Documents`、
`Strata.Infrastructure.Persistence`、`Strata.Api.Controllers`。

## 3. 領域模型

```
Tenant 1 ──── * ApplicationUser
   │                 │
   │ (TenantId)      │ (OwnerId)
   ▼                 ▼
Folder * ────────► Document * ────────► DocumentShare
   ▲ ParentFolderId    (FolderId)          (DocumentId, UserId)
   └── 自我參照
```

| Entity | 主要欄位 | 備註 |
|---|---|---|
| `Tenant` | `Id`、`Name`（≤200、必填）、`CreatedAt` | 不受租戶 filter 影響 |
| `ApplicationUser` | `IdentityUser<Guid>` + `TenantId` | 以 `Guid` 為 key；不受租戶 filter 影響 |
| `Folder` | `Id`、`OwnerId`、`TenantId`、`ParentFolderId?`、`Name` | 自我參照的層級結構 |
| `Document` | `Id`、`OwnerId`、`TenantId`、`FolderId?`、`Name`、`Size`、`ContentType` | 實際 bytes 存於 Blob Storage，以 `Id` 命名 |
| `DocumentShare` | `Id`、`DocumentId`、`UserId`、`TenantId`、`UserRole` | `UserRole` ∈ {`Member`、`Viewer`} |

`Strata.Domain` 內兩個 marker interface 驅動橫切行為：

- `IOwnable`（`Guid OwnerId`）— 供 `OwnerAuthorizationHandler` 使用。
- `ITenantOwned`（`Guid TenantId`）— 供 write interceptor 使用。

`Id`、`OwnerId`、`TenantId` 都是 `init`-only。注意 `init` 只阻止對「已建構完成的
instance」直接用 C# 賦值——它**不會**阻止
`Update(new Document { ... })` attach 一個 `TenantId` 可以寫成任何值的 detached
entity，這正是 write interceptor 也必須驗證 `Modified` 的原因（見 §6.4）。

### 關聯設定

所有 foreign key 都用 `DeleteBehavior.Restrict`，只有
`DocumentShare → Document` 例外，採用 cascade（刪除 document 會一併移除其
share）。

| Table | Index |
|---|---|
| `Folders` | `TenantId` |
| `Documents` | `TenantId` |
| `DocumentShares` | `TenantId`；unique `(DocumentId, UserId)` |
| `AspNetUsers` | `TenantId` |

Unique `(DocumentId, UserId)` index 才是防止重複 share 的真正保障；應用層的預先檢
查只是最佳化。`CreateShare` 在捕捉到 `DbUpdateException` 之後會**重新驗證**，而不
是假設一定是那條 constraint 造成——不相干的失敗絕不能被誤報成「已經分享過」。

## 4. Migration 歷史

按順序套用；每個 migration 在套用前都經人手 review，且從不由 CI 自動套用。

| Migration | 內容 |
|---|---|
| `20260902113900_InitialCreate` | Identity 相關 table、`Folders`、`Documents` |
| `20260904102714_AddDocumentSharesUniqueIndex` | Unique `(DocumentId, UserId)` |
| `20260905020127_AddTenant` | 只加 `Tenants` table |
| `20260905025451_AddApplicationUserTenantId` | 必填的 `ApplicationUser.TenantId`，既有使用者回填至一個決定性的 Legacy Tenant |
| `20260905051133_AddTenantIdToResources` | `Folders` / `Documents` / `DocumentShares` 加上必填 `TenantId` |

### 分階段、fail-closed 的 migration 模式

當一個已有資料的 table 要加必填欄位時，`dotnet ef migrations add` 會產生
`NOT NULL DEFAULT Guid.Empty`。那個 default 是無聲的錯誤：它憑空發明了一個租戶。
兩個租戶相關的 migration 都被人手改寫成五個階段：

1. 先加成 **nullable** 欄位。
2. **從真實關聯回填**，不用 sentinel 值——`Folders`／`Documents` 由
   `OwnerId → AspNetUsers.TenantId`；`DocumentShares` 由
   `DocumentId → Documents.TenantId`（share 跟隨它的 **document**，絕不跟隨接收
   者，因此既有的跨租戶接收者會被保留，而不是被改寫）。
3. **驗證並 fail closed**——若仍有 null 值、或子 folder 與父 folder 不一致、或
   document 與其 folder 不一致，`RAISERROR` 會中止整個 migration。
4. `ALTER COLUMN ... NOT NULL`。
5. 加 index 與 foreign key。任何地方都不留下永久性的 default constraint。

`AddTenantIdToResources` 曾在一個「先 migrate 到 N−1、再植入具代表性跨租戶資料」
的拋棄式資料庫上完整綵排過，並包含一個刻意的反面測試，證明 `RAISERROR` 檢查真的
會中止並且不留下任何部分變更。

## 5. Request pipeline 與 DI

`Program.cs` 依序：controllers → health checks → OpenAPI →
`TenantWriteGuardInterceptor`（scoped）→ `AppDbContext`（掛上 interceptor）→
`IApplicationDbContext` → Identity core → JWT bearer → `JwtTokenGenerator` →
`IHttpContextAccessor` → `ICurrentTenant` → `IFileStorage` → authorization
handlers。

| Service | 生命週期 | 原因 |
|---|---|---|
| `AppDbContext` / `IApplicationDbContext` | Scoped | 每個 request 一個 |
| `TenantWriteGuardInterceptor` | Scoped | 相依於 scoped 的 `ICurrentTenant` |
| `ICurrentTenant` → `HttpContextCurrentTenant` | Scoped | 每個 request 的租戶身分 |
| `IFileStorage` → `BlobFileStorage` | Singleton | 無狀態，不持有 per-request 狀態 |
| `OwnerAuthorizationHandler` | Singleton | 只對傳入的 resource 做純檢查，無 I/O |
| `DocumentAccess` / `DocumentEdit` handler | Scoped | 需要查 `DocumentShares`，所以需要 DbContext |

`AddDbContext` 特意採用 `(sp, options)` 的 overload，好讓 interceptor 能從
request scope 解析出來——單一參數的 overload 沒有 `IServiceProvider`，取不到
scoped 的 `ICurrentTenant`。

Pipeline：`UseHttpsRedirection` → `UseAuthentication` → `UseAuthorization` →
controllers + `/health`。

## 6. 關鍵機制

### 6.1 租戶 claim 驗證 — `Program.cs` + `TenantClaimTypes`

`TenantClaimTypes.TryGetValidTenantId`（位於 `Strata.Application.Tenancy`，令
token 簽發者與兩個強制點共用同一份定義）要求：**剛好一個** `tenant_id` claim、可
以 parse 成 `Guid`、而且不等於 `Guid.Empty`。它從不在多個之中取第一個。

它在 JWT bearer 的 `OnTokenValidated` event 執行，因此不通過該檢查的 token 會在
authentication 邊界以 401 被拒，永遠到不了 controller。`MapInboundClaims = false`
令 claim type 不被改寫（`sub` 保持是 `sub`）。

### 6.2 `ICurrentTenant` — `Strata.Api/Tenancy/HttpContextCurrentTenant.cs`

```csharp
public bool IsAvailable => _httpContextAccessor.HttpContext is not null;
public Guid TenantId { get; }   // 每次讀取時解析；無效則 throw
```

解析是**延後**的——constructor 只儲存 accessor。正因如此，需要
`ICurrentTenant` 的 `AppDbContext` 才能在 HTTP request 以外被建構，而 migrations
與測試設定正需要這一點。Fail-closed 的性質沒有失去，只是移到「真正讀取租戶身分」
的那一刻。

`IsAvailable` 回答的是「究竟有沒有一個 request」，**不是**「claim 是否有效」——一
個帶壞 claim 的 request 會回報 `true`，然後在讀 `TenantId` 時 throw，因為那種情況
過了 §6.1 之後理應不可能發生，絕不能被靜靜跳過。

### 6.3 Global query filters — `AppDbContext.OnModelCreating`

```csharp
builder.Entity<Folder>().HasQueryFilter(f => f.TenantId == _currentTenant.TenantId);
builder.Entity<Document>().HasQueryFilter(d => d.TenantId == _currentTenant.TenantId);
builder.Entity<DocumentShare>().HasQueryFilter(s => s.TenantId == _currentTenant.TenantId);
```

每條 filter 閉包捕捉的是 `_currentTenant`（注入的 service，而不是複製出來的
`Guid`），所以 EF Core 會在每次 query 時針對當下這個 `DbContext` instance 重新求
值，而不是在 model 建立時把值寫死。

`Tenant` 與 `ApplicationUser` 刻意不加 filter。

對查找方式的後果：任何租戶資源都**不**使用 `FindAsync`，因為它可能不查 database
就直接傳回已被追蹤的 entity——那樣 filter 根本不會生效。所有查找一律用
`SingleOrDefaultAsync(x => x.Id == id, ct)`。

### 6.4 Write interceptor — `TenantWriteGuardInterceptor`

實作 `SaveChangesInterceptor`，同時 override `SavingChanges` 與
`SavingChangesAsync`。對每個狀態為 `Added`、`Modified`、`Deleted` 的
`ITenantOwned` entry：

1. 若 `!_currentTenant.IsAvailable` → throw。在沒有可信租戶的情況下寫入租戶資料
   一律拒絕，絕不當作隱含的管理員通道。
2. 若為 `Deleted` 或 `Modified` → 用 `EntityEntry.GetDatabaseValuesAsync()` 讀取
   該行**真實**的當前 `TenantId`，並與當前租戶比較。`GetDatabaseValues` 刻意忽略
   query filter（它的存在意義就是回報 database 的真實狀態），所以外租戶的資料會
   帶著真實 `TenantId` 回來，然後被那個明確的比較拒絕。傳回 `null` 代表該行真的
   不存在。
3. 若為 `Added` 或 `Modified` → 同時比較**準備寫入的值**。這一步才是阻止「把自己
   的資料改寫成屬於另一個租戶」的關鍵。

它只驗證，從不指派 `TenantId`。

Migration 永遠不會走到這段程式碼——schema DDL 與 `migrationBuilder.Sql` 回填都不
經過 `SaveChanges`，也不經過 change tracker。

### 6.5 Authorization handlers — `Strata.Api/Authorization/`

| Requirement | Handler | 規則 |
|---|---|---|
| `OwnerRequirement` | `OwnerAuthorizationHandler` | `sub` claim == `resource.OwnerId`。純檢查，無 I/O。 |
| `DocumentAccessRequirement` | `DocumentAccessAuthorizationHandler` | Owner，**或** 該使用者有任何 `DocumentShare` |
| `DocumentEditRequirement` | `DocumentEditAuthorizationHandler` | Owner，**或** 一個 `Role.Member` 的 share |

兩個與 share 相關的 handler 都透過同一個 `IApplicationDbContext` 查
`DocumentShares`，所以那些 query 自動繼承了租戶 filter——它們裡面不需要另寫租戶邏
輯。

## 7. API 介面

| Method | 路由 | 授權 | 備註 |
|---|---|---|---|
| `POST` | `/api/auth/register` | 匿名 | 原子性地同時建立 `Tenant` 與其第一個使用者；回傳 JWT |
| `POST` | `/api/auth/login` | 匿名 | 回傳 JWT |
| `GET` | `/api/folders` | JWT | 呼叫者自己的 folder |
| `POST` | `/api/folders` | JWT | `TenantId` 由 server 端從 `ICurrentTenant` 蓋上 |
| `PUT` | `/api/folders/{id}` | JWT、owner | 改名／搬移，含循環偵測 |
| `DELETE` | `/api/folders/{id}` | JWT、owner | 非空則 409 |
| `POST` | `/api/documents` | JWT | 回傳 `documentId` 與一個 15 分鐘的上傳 SAS URI |
| `GET` | `/api/documents/{id}/download` | JWT、owner 或 share | 回傳 15 分鐘的下載 SAS URI |
| `PUT` | `/api/documents/{id}` | JWT、owner 或 `Member` | 改名 |
| `POST` | `/api/documents/{id}/shares` | JWT、owner | 重複則 409 |
| `GET` | `/api/documents/{id}/shares` | JWT、owner | |
| `DELETE` | `/api/documents/{id}/shares/{shareId}` | JWT、owner | |
| `GET` | `/health` | 匿名 | |

註冊不需要顯式 transaction 就已是原子操作：`Tenant` 先被標記為 `Added`，而
Identity 自己的 `SaveChangesAsync`——只在它所有驗證通過之後才執行——會把兩者在同一
個 transaction 內一併寫入。

每條 create path 都由 server 端從 `ICurrentTenant` 指派 `TenantId`；client 自行送
上來的 `tenantId` 欄位完全無效（有測試覆蓋）。Share 蓋的是**它 document 的**租
戶，絕不是接收者的。

不存在與無權限的資源同樣回 404（防列舉）。

## 8. 測試

共 89 條測試：2 條架構測試 + 87 條整合測試。

### 架構測試（`Strata.Architecture.Tests`）

NetArchTest 斷言 `Strata.Domain` 對 `Strata.Application`、
`Strata.Infrastructure`、`Strata.Api`、EF Core、ASP.NET Core 全部沒有相依；以及
`Strata.Application` 對 `Strata.Infrastructure` 與 `Strata.Api` 沒有相依。分層是
被強制執行的，不只寫在文檔裡。

### 整合測試（`Strata.Api.IntegrationTests`）

透過 `WebApplicationFactory<Program>` 對真實 SQL Server（本機用 Docker，CI 用
service container）發出真實 HTTP request——不使用 in-memory provider，因此 unique
index、FK 行為與真實 SQL 語義都真正被驗證到。

| 組件 | 角色 |
|---|---|
| `IntegrationTestFixture` | 每個 collection 一個已 migrate 的資料庫；每條測試前用 Respawn 重設資料 |
| `StrataWebApplicationFactory` | 託管真實的 app；把 `IFileStorage` 換成 `FakeFileStorage` |
| `FakeFileStorage` | 統計上／下載呼叫次數，好讓測試可以斷言**沒有**碰過 Blob Storage |
| `TestApiHelpers` | 註冊／認證、建立 folder／document／share、手工簽發 JWT |
| `FixedCurrentTenant` / `NoCurrentTenant` | `ICurrentTenant` 的測試替身 |

Fixture 有兩個刻意保留的逃生口：

- `QueryDbAsync(...)`——用一個新的 scoped `AppDbContext` 做管理性斷言。所有針對租
  戶資料的這類讀取都明確呼叫 `IgnoreQueryFilters()`，因此測試中的 filter 繞過永遠
  在呼叫處清楚可見。它沒有 `HttpContext`，所以任何經它進行的租戶資料**寫入**都會
  fail closed。
- `CreateDbContext(ICurrentTenant)`——建立一個掛著真實 interceptor、並由呼叫方指定
  租戶身分的 `AppDbContext`，繞過 DI／HTTP。這是測試「扮演某個特定租戶」的方式，
  也是唯一能觸發 interceptor 各條 throw 路徑的方法。

主要測試群組：

- `TenantClaimAuthenticationTests`——缺失／格式錯誤／空值／重複的 `tenant_id`
  claim 全部得到 401，經真實 HTTP 與真正簽章的 token 驗證。
- `HttpContextCurrentTenantTests`——延後解析、fail-closed 存取、建構永不 throw、
  `IsAvailable` 的語義。
- `TenantReadIsolationTests`——對抗性資料：`OwnerId` 是操作者自己的，但 `TenantId`
  是另一個真實租戶，因此 owner authorization **本來會**放行。涵蓋列表、folder 更
  新／刪除、document 建立於 folder 內／下載、以及 share 的建立／列出／刪除，每一
  條都斷言資料庫沒有變動、且沒有呼叫過 Blob Storage。
- `TenantWriteIsolationTests`——直接針對 interceptor：新增時帶外租戶 `TenantId`；
  以該行真實租戶值的 stub 刪除；以**偽造成當前租戶**的 stub 刪除；`Update` 把
  A→B 搬遷；對外租戶資料以偽造 id 做 `Update`；沒有租戶 context 的寫入；以及各自
  的合法對照組，另加一條覆蓋同步 `SaveChanges` 路徑。
- 分享行為——同租戶 Member／Viewer 的測試直接用 `UserManager` 植入一個同租戶使用
  者，因為每次呼叫 `/api/auth/register` 都會鑄造一個全新租戶，因此不可能產生兩個
  同租戶的使用者。

## 9. 本機執行（Windows）

前置需求：Git、符合 `global.json` 的 .NET SDK、Docker Desktop（WSL 2、Linux
containers）、Azure CLI。

```powershell
git clone https://github.com/maxtsec/strata.git
Set-Location strata

docker run --name strata-sql --hostname strata-sql `
  -e "ACCEPT_EULA=Y" `
  -e "MSSQL_SA_PASSWORD=Strata_Dev_2026!" `
  -p 127.0.0.1:1433:1433 `
  -v strata-sql-data:/var/opt/mssql `
  -d mcr.microsoft.com/mssql/server:2022-latest
```

`Strata_Dev_2026!` 是公開已知的本機／測試專用憑證；port 只綁定 loopback，而且絕不
可在 Azure 或任何真實環境重用。

```powershell
dotnet tool restore
dotnet restore

$jwtBytes = New-Object byte[] 48
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($jwtBytes)
$jwtKey = [Convert]::ToBase64String($jwtBytes)
$rng.Dispose()

dotnet user-secrets set 'ConnectionStrings:DefaultConnection' 'Server=tcp:127.0.0.1,1433;Database=Strata;User Id=sa;Password=Strata_Dev_2026!;TrustServerCertificate=True;MultipleActiveResultSets=true' --project src/Strata.Api
dotnet user-secrets set 'Jwt:SigningKey' $jwtKey --project src/Strata.Api
az login
dotnet ef database update --project src/Strata.Infrastructure --startup-project src/Strata.Api
```

用 `127.0.0.1` 而不是 `localhost`——在 Windows 上，`localhost` 可能解析成 IPv6，因
而連不上 container 的 IPv4 port binding。

`az login` 令 `DefaultAzureCredential` 能在本機存取 Blob Storage；部署後的程式碼
則改用 App Service 的 managed identity。

每次開工：

```powershell
docker start strata-sql
dotnet build
dotnet test
dotnet run --project src/Strata.Api
```

`dotnet test` 會用同一個 container 上的第二個資料庫
（`StrataIntegrationTests`）。若本機密碼不同，用 `STRATA_TEST_CONNECTION_STRING`
覆寫連線字串。

## 10. Azure 資源與部署

| 資源 | 名稱 | 備註 |
|---|---|---|
| Resource group | `rg-strata-dev` | |
| App Service | `strata-api` | Linux、.NET 10；已啟用 `httpsOnly` |
| SQL Server／DB | `sql-strata-dev` / `strata` | Entra ID admin；app 不使用靜態 SQL login |
| Key Vault | — | 存放連線字串；App Service 以 managed identity 讀取 |
| Blob Storage | — | User-delegation SAS；app 身分需要 Storage Blob Data Contributor |

### CI/CD

`.github/workflows/deploy.yml`：

- **build-and-test**——push 到 `main` 以及每個 PR 都會跑。還原、Release build、
  對一個 SQL Server 2022 service container 跑完整測試套件。
- **deploy**——只在 push 到 `main` 且測試通過之後跑。先 `dotnet publish`，再經
  **OIDC** 用 `azure/login@v2`（`id-token: write`；GitHub 內不存放任何長期 Azure
  憑證），然後 `azure/webapps-deploy@v3`。

Migration **永不**由 CI 套用。

Git 流程：每個邏輯工作單位一條 feature branch → PR → CI 綠燈 → **rebase and
merge** → 刪除 branch。絕不直接 commit 上 `main`。

### Key Vault reference 的一個真實坑

App Service 的 Key Vault reference 若鎖定了 secret 的**版本**
（`@Microsoft.KeyVault(SecretUri=https://…/secrets/Name/<version>)`），它永遠不會
取到新設定的版本。省略版本片段，reference 才會一直解析到當前版本。這個坑曾耗掉真
實的除錯時間，值得在再次踩到之前先知道。

### 對 Azure SQL 套用 migration

由開發機按需執行。Azure SQL 的認證方式與 Blob Storage 一致——經 `az login` 的
Entra ID 身分，不用靜態 SQL login。

```powershell
az sql server ad-admin list --server sql-strata-dev --resource-group rg-strata-dev
```

套用時需要把當前 IP 開通過 server firewall，僅限該指令執行期間——而且事後不能留下
任何開放，包括 migration 本身失敗的情況。`try`/`finally` 保證兩種情況下規則都會被
刪除：

```powershell
$myIp = (Invoke-RestMethod -Uri "https://api.ipify.org?format=json").ip
az sql server firewall-rule create --server sql-strata-dev --resource-group rg-strata-dev `
  --name "dev-temp" --start-ip-address $myIp --end-ip-address $myIp

try {
  dotnet ef database update --project src/Strata.Infrastructure --startup-project src/Strata.Api `
    --connection "Server=tcp:sql-strata-dev.database.windows.net,1433;Database=strata;Authentication=Active Directory Default;TrustServerCertificate=False;Encrypt=True;"

  dotnet ef migrations list --project src/Strata.Infrastructure --startup-project src/Strata.Api `
    --connection "Server=tcp:sql-strata-dev.database.windows.net,1433;Database=strata;Authentication=Active Directory Default;TrustServerCertificate=False;Encrypt=True;"
}
finally {
  az sql server firewall-rule delete --server sql-strata-dev --resource-group rg-strata-dev --name "dev-temp"
}
```

`Authentication=Active Directory Default` 解析的是與 `DefaultAzureCredential` 相
同的憑證鏈。這與已部署的 app 在 runtime 如何連線是兩回事（那條連線字串留在 Key
Vault）。

### 受控維護部署

當新 schema 與舊版應用程式不相容時——例如加入舊版 app 不會填的 `NOT NULL
TenantId` 欄位——次序很重要：

1. 停止 App Service，並確認它真的已停止。
2. 從已 review 的那個 commit 套用已 review 的 migration。
3. 以唯讀方式驗證 schema：migration 記錄只出現一次、欄位為 `NOT NULL`、沒有 default
   constraint、沒有 null／`Guid.Empty` 值、關聯一致、index 與 foreign key 齊備。
4. 確認無誤後才 merge，並讓 deployment 執行。
5. 啟動 app，檢查 `/health` 與啟動 log，再查一次 migration 歷史。

先停止應用程式，是為了消除「舊版 app 對著新 schema 執行」那段時間窗——它無法插入
缺少新必填欄位的資料，於是這個失敗模式是被移除，而不是靠賽跑僥倖避開。

## 11. 慣例

- 全面 async；任何跨越邊界的呼叫都帶 `CancellationToken`。
- 原始碼與 `appsettings` 內不存放機密——用 Key Vault，本機用 user-secrets。
- 每個 migration 套用前經 review；絕不由 CI 自動套用。
- 結構化 logging（Serilog），絕不用 `Console.WriteLine`。
- Conventional commits。
- `Strata.Domain` 不得 reference EF Core、Azure SDK 或 ASP.NET——連一個 `using`
  都不行。分層是否名副其實的檢驗標準：業務規則應該在什麼都不啟動的情況下就能做
  unit test。
