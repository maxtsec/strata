# Strata — 設計文檔

> 相關文檔：[技術文檔（中文）](technical.zh.md) ·
> [Design Document (English)](design.md) ·
> [Technical Document (English)](technical.md)

## 1. Strata 是什麼

Strata 是一個多租戶（multi-tenant）文件協作平台：多個客戶組織共用同一套
deployment，而每個組織的資料與其他組織完全隔離。後期階段會在每個租戶自己的
文件之上加一層 AI 問答功能。

名字取自澳洲的 *strata title*（分層地契）：一幢樓、多個獨立擁有的單位、共用
的結構。那正是多租戶在物理世界的樣子。

這是一個 portfolio 項目，目的是展示企業級架構與安全優先的工程思維，而不是要
推出一件產品。凡是走了捷徑的地方，本文檔會直接寫出來，而不是掩蓋。

## 2. 核心問題

Strata 其他所有設計，都是為了回答同一條問題：

> 當租戶 A 的使用者發出請求時，是否存在任何路徑——透過 id、透過偽造的
> payload、或透過將來的某次程式碼改動——可以觸及租戶 B 的資料？

有兩個特性令這件事真正困難，而不只是繁瑣：

1. **隔離是應用程式碼的性質，不是基礎設施的性質。** 所有租戶共用同一個
   database、同一組 table（見 §4）。沒有 firewall、沒有獨立 connection
   string、沒有 database 邊界會阻止一個 query 跨租戶讀取資料。只有程式碼會。
2. **隔離必須在「從沒讀過這份文檔的人」手上仍然成立。** 一個今天有效、但將來
   任何一個新功能都可以無聲繞過的機制，並不是隔離，只是一個懷有善意的慣例。

第二點正是整個設計依賴 **defence in depth**（縱深防禦，§5）與**對抗性測試**，
而不依賴任何單一機制的原因。

## 3. 架構

Clean Architecture，相依方向只准向內：

```
Strata.Api  ──────────────┐
    │                     │
    ▼                     ▼
Strata.Application   Strata.Infrastructure
    │                     │
    ▼                     │
Strata.Domain  ◄──────────┘
```

| 專案 | 職責 |
|---|---|
| `Strata.Domain` | Entity 與業務規則。不含 EF Core、Azure SDK、ASP.NET。 |
| `Strata.Application` | Use case；宣告自己需要的 interface（`IApplicationDbContext`、`IFileStorage`、`ICurrentTenant`）。 |
| `Strata.Infrastructure` | EF Core（`AppDbContext`、migrations）、Blob Storage、ASP.NET Core Identity 的 `ApplicationUser`。實作 Application 宣告的 interface。 |
| `Strata.Api` | Controller、request validation、DI wiring。唯一直接 reference `Strata.Infrastructure` 的專案。 |

**為什麼這個方向在此處特別重要。** 租戶隔離規則住在資料存取所在的地方
（`Strata.Infrastructure`），但「現在這個 request 屬於哪個租戶」這個*概念*是由
`Strata.Application` 以 `ICurrentTenant` 宣告、由 `Strata.Api` 從 HTTP request
實作的。正是這個反轉，令 persistence layer 能夠執行一條「真相來源是 HTTP 概念」
的規則，而 `Strata.Infrastructure` 完全不需要知道 HTTP 的存在。

這個方向由 `tests/Strata.Architecture.Tests`（NetArchTest）強制執行，不只是寫
在文檔上——測試會斷言 `Strata.Domain` 對 EF Core 與 ASP.NET 完全沒有相依。

### 刻意不做的抽象

Strata **沒有** repository layer 包住 EF Core
（[ADR 0001](adr/0001-no-repository-abstraction.md)）。`DbSet<T>` 本身就是
Repository pattern，`DbContext` 本身就是 Unit of Work；再包一層等於重新實作
framework 已經提供的抽象，破壞 `IQueryable` 組合能力，而且會隨需求增長變成
「每一種 query 形狀一個 method」。`Strata.Application` 改為宣告一個薄的
`IApplicationDbContext`，只暴露 `DbSet<T>` 與 `SaveChangesAsync`。

項目採用的通則：一個只有一個實作、而且看不到第二個實作可能性的 interface，不值
它的成本。能夠解釋為什麼**沒有**抽象某樣東西，在此被視為比展示「我懂得抽象」更
有價值。

## 4. 租戶模型

**共用 database、共用 schema、以 `TenantId` 欄位作區分**
（[ADR 0004](adr/0004-shared-database-shared-schema-tenancy.md)）。

每一行屬於租戶的資料都帶一個 `TenantId` foreign key，指向 `Tenants` table。每個
`ApplicationUser` 屬於且只屬於一個租戶。

考慮過並否決了兩個方案：

- **Database per tenant** 隔離性最強——query filter 的 bug 無法跨越 database
  邊界。因經濟與營運考量否決：每次 schema 改動變成 N 次 deployment，而 Azure
  SQL 的單 database 成本下限令幾個示範租戶變得毫無必要地昂貴。當隔離屬於合約或
  法規要求時它是正確答案，但這裡並沒有這種需求。
- **Schema per tenant** 消除了共用 table 的風險，但沒有消除租戶邊界的風險——失
  敗模式只是從「忘記加 row filter」變成「切錯 schema」。主要否決原因是 EF Core
  沒有一等公民級的動態 per-tenant schema 支援：migrations 與 model snapshot 都
  假設固定 schema，採用它等於與工具對抗。

選擇共用 schema，一部分是因為工具鏈對它支援良好、而且它是這個規模下業界常見的
SaaS 形狀；另一部分——也是對本項目目的最相關的——是因為它正是那種**隔離保證必
須在應用程式碼中親手掙回來**的多租戶。那正是這個項目想示範的課題。

## 5. 隔離模型：四層互相獨立的防線

不信任任何單一機制。以下每一層堵住不同的缺口，而且每一層都同時寫明它**不**負責
什麼。

### 第 1 層 — 在 authentication 邊界確立可信的租戶身分

當前租戶來自一個已簽章、已驗證的 JWT 內的 `tenant_id` claim——絕不來自 request
body、query string 或任何由 client 控制的 header。

驗證發生在 JWT bearer 的 `OnTokenValidated` event，即 authentication 邊界本身，
而不是延後到某個 service 內部。一個其他方面完全有效、但 tenant claim 缺失、格式
錯誤、等於 `Guid.Empty`、或出現多過一次的 token，會在 authentication 階段以 401
失敗，永遠不會到達 controller。拒絕**重複**的 claim 很重要：若默默取多個之中的第
一個，一個精心構造的 token 就可以偷渡第二個租戶身分。

這是一個 claim 檢查，不是 database lookup，所以成本很低，而且不需要為了確立租戶
身分而先去讀租戶資料。

*不負責：* 任何不是經 HTTP 進來的東西。

### 第 2 層 — Request-scoped 的可信租戶 context

`ICurrentTenant`（在 `Strata.Application` 宣告，在 `Strata.Api` 由
`HttpContextCurrentTenant` 實作）是「這個 request 以哪個租戶身分行動」的唯一真相
來源。下游所有東西——controller 為新資料蓋上租戶標記、query filter、write
interceptor——一律從它讀取，而不是各自重新推導租戶身分。

它是延後解析（lazy）而且**fail closed**：在沒有有效 request 的情況下讀取
`TenantId` 會 throw，而不是給一個 default 值。另有一個 `IsAvailable` property，
用來區分兩種絕不可混為一談的情況：

| 情況 | `IsAvailable` | 意義 |
|---|---|---|
| 根本沒有 HTTP request | `false` | Migrations、測試／管理性設定——「當前租戶」這條問題本身不適用 |
| 有 request 但 claim 無效 | `true`，讀 `TenantId` 會 throw | 過了第 1 層之後理應不可能發生——即 bug 或被竄改的 token |

之所以刻意採用延後解析，是因為 `AppDbContext` 的 constructor 需要一個
`ICurrentTenant`；唯有延後解析，`AppDbContext` 才能在沒有 HTTP request 的情況下
被建構，而 migrations 與測試設定正正需要這一點。

### 第 3 層 — EF Core global query filters（讀取）

`Folder`、`Document`、`DocumentShare` 各自帶一條
`TenantId == currentTenant.TenantId` 的 filter，預設套用在針對這些 set 的每一條
LINQ query 上。

兩個值得說明的設計選擇：

- **逐個 entity 明確寫出，而不是用 reflection convention。** 三個固定的 entity
  不值得引入一套 compiler 檢查不到、讀者也看不見的 expression tree 機器。將來若
  第四個 entity 漏了 filter，那是檔案裡一行可見的空缺；reflection 適合用在斷言
  「每個 `ITenantOwned` 都有 filter」的 architecture **測試**上，而不是 production
  的 wiring。
- **`Tenant` 與 `ApplicationUser` 刻意不加 filter。** Authentication 必須能在租戶
  身分成立之前先找到 user，而分享對象（recipient）查找是另一件事（§7）。

由於 filter 只作用於 LINQ query 路徑，所有具安全意義的查找都已移除
`FindAsync`：`FindAsync` 可能直接從 change tracker 傳回一個已被追蹤的 entity 而
完全不查 database，那樣 filter 就從來沒有機會生效。

*不負責：* 任何寫入操作；任何呼叫了 `IgnoreQueryFilters()` 的 query。

### 第 4 層 — `SaveChanges` interceptor（寫入）

Query filter 只改寫 `SELECT`，僅此而已。`TenantWriteGuardInterceptor` 會在資料真
正寫入之前，檢查每一個狀態為 `Added`、`Modified`、`Deleted` 的租戶擁有 entity，
只要有一個不屬於當前租戶，就拒絕整次 `SaveChanges`。

它套用的規則，以及每條規則的存在理由：

| Entity 狀態 | 檢查對象 | 堵住的攻擊 |
|---|---|---|
| `Added` | 準備寫入的值 | 新資料被蓋上別人的租戶標記 |
| `Modified` | **database 內實際那一行** 以及 準備寫入的值 | 憑 id 竊取外租戶資料；把自己的資料搬去另一個租戶 |
| `Deleted` | **database 內實際那一行** | 憑 id 刪除外租戶資料 |

關鍵細節在於：對 `Modified` 與 `Deleted`，interceptor **不信任** entity 自己聲稱
的 `TenantId`。一個從未被查詢過就 attach 上去的 entity——例如
`Remove(new Document { Id = someId })`、`Update(new Document { ... })`——帶的是呼
叫者自己填的值，包括一個刻意偽造成與當前租戶相符的 `TenantId`。而 EF Core 產生的
`DELETE`／`UPDATE` 條件是 primary key，不是 `TenantId`，所以若信任那個聲稱值，外
租戶的資料就會被刪掉。因此 interceptor 改為用
`EntityEntry.GetDatabaseValuesAsync()` 讀取該行在 database 內的真實狀態，並以此
為準。

它**只驗證，從不指派** `TenantId`。自動蓋章（auto-stamping）會令「租戶身分在哪
裡決定」變成兩個地方，那比在 create path 多寫幾行明確賦值更差。

在沒有可信租戶身分的情況下進行租戶資料寫入，會 **fail closed**（直接拒絕），而不
是當作隱含的可信管理員通道——這份設計的早期版本正正把這點寫反了，而抓到它的那次
review，正是這條規則在此被如此明確寫出的原因。

*不負責：* `ExecuteUpdate` / `ExecuteDelete` / raw SQL（§7）。

### 四層如何合起來運作

以「租戶 A 的 request 嘗試憑 id 觸及租戶 B 的 document」為例：

1. 第 1 層已確立呼叫者真的是以租戶 A 身分行動。
2. 第 3 層令該 document 對 controller 的查找完全不可見，因此在 authorization 執行
   之前就已經 404。
3. 若將來某段程式碼繞過了查找、直接 attach 該行，第 4 層會透過查 database 本身而
   拒絕該次寫入。
4. Owner authorization（§6）是第四道獨立障礙——但它**不**被計算為租戶隔離，因為它
   回答的是另一條問題。

## 6. 認證與授權

**認證**（[ADR 0002](adr/0002-jwt-bearer-authentication.md)）：由 ASP.NET Core
Identity 儲存與雜湊憑證；認證本身則是無狀態的簽章 JWT（HMAC-SHA256，一小時到
期）。`ApplicationUser` 以 `Guid` 為 key，因此與 `OwnerId` 對得上，任何邊界都不需
要 string↔Guid 轉換。

選擇 bearer token 而非 cookie，是因為 Strata 是 JSON API；而且放在
`Authorization` header 的 bearer token 不會被瀏覽器自動附帶——這從結構上消除了
CSRF 問題，而不是需要 anti-forgery token 去解決它。

**授權**是 resource-based（`IAuthorizationHandler` 針對已載入的 entity 判斷），而
不是靠 role 字串：

| 角色 | 下載 | 改名 | 管理分享 |
|---|---|---|---|
| Owner | 可 | 可 | 可 |
| Member（經分享） | 可 | 可 | 不可 |
| Viewer（經分享） | 可 | 不可 | 不可 |

不存在的資源與無權限的資源同樣回 404 而非 403，因此無法透過回應去試探哪些 id 存在
（anti-enumeration，防列舉）。

**Owner authorization 不等於租戶隔離。** 它今天碰巧擋住了大部分跨租戶存取，因為
owner 是逐個使用者的；但它回答的是「這是不是正確的使用者？」，而且從來不查
`TenantId`。把它當成隔離邊界，正是這份設計致力避免的那種概念錯置——所以第 3 層與
第 4 層獨立於它而存在。

## 7. 已知缺口

坦白列出，因為寫下來的缺口是設計決定，沒寫下來的缺口是缺陷。

- **跨租戶分享在建立時不會被拒絕。** 租戶 A 仍然可以建立一個指名租戶 B 使用者的
  share。該 share 會被蓋上**document 的**租戶標記（絕不會用接收者的），所以它不
  會把 document 洗進另一個租戶；而 read filter 令接收者根本用不到它——它是一行死
  資料，不是洩漏。Same-tenant relationship enforcement 是下一項計劃中的工作。
- **`ExecuteUpdate` / `ExecuteDelete` / raw SQL 同時繞過第 3、4 層。** 集合式語句
  從不把 entity 載入 change tracker，所以 interceptor 看不到它們；query filter 能
  限制這類語句讀取哪些 row，但管不到它賦予什麼值。項目政策：這三者連同
  `IgnoreQueryFilters`，未經獨立的租戶隔離設計 review、明確的強制機制與對抗性測試
  之前，不得用於租戶資料。目前的圍堵是政策，不是程式碼。
- **營運與特權 database 存取繞過一切。** 用 SSMS 或帶 SQL admin 憑證的支援腳本跑
  的 query，完全在應用程式的 query surface 之外。Query filter 與 interceptor 不是
  database 層級的存取控制。
- **寫入檢查是 save 前驗證，不是 SQL 層的條件。** 檢查與寫入之間理論上存在時間
  窗。以目前規模可接受，但若將來要支援 tenant transfer，這個 race 需要重新處理。
- **JWT 在到期前無法撤銷。** 這是無狀態 bearer token 的標準取捨；以一小時的短窗
  緩解，而非解決。
- **已發出的 SAS URI 在 share 被撤銷後仍可用**，最長至其 15 分鐘窗口結束
  （[ADR 0003](adr/0003-blob-storage-user-delegation-sas.md)）。這是刻意接受的代
  價，換取不必把檔案流量代理經過 API 而令每個 byte 的頻寬與運算成本翻倍。
- **`ValidateIssuer` / `ValidateAudience` 目前關閉。** 在只有一個 issuer 與一個
  audience 的情況下安全；但在任何第二個服務共用或信任該簽章金鑰之前，必須先修好。

## 8. 階段

| 階段 | 範圍 | 狀態 |
|---|---|---|
| 0 | Walking skeleton：solution 結構、`/health`、部署上 Azure、CI/CD 綠燈、Key Vault | 完成 |
| 1 | 單租戶核心：使用者、folder、document、認證、Blob Storage 上下載、角色、分享連結 | 完成 |
| 2 | 多租戶改造：`Tenant`、`TenantId` 欄位、租戶解析、query filters、write interceptor、對抗性測試 | 進行中 |
| 3 | 通知子系統：`INotificationChannel`、背景派送、指數退避重試、idempotency | 計劃中 |
| 4 | Production hardening：結構化 logging、全域例外處理、rate limiting | 計劃中 |
| 5 | 多租戶 RAG：ingestion、按租戶過濾的向量檢索、生成前二次檢查、成本／配額 | 計劃中 |
| 6 | Packaging：架構圖、截圖、口頭講解 | 計劃中 |

第 1 階段刻意建成單租戶，好讓第 2 階段是一次真正的改造（retrofit）。做這次改造
——包括那個分階段、fail-closed、從真實關聯回填既有資料的 migration——本身就是預期
中的課題，不是排序上的意外。

**第 2 階段餘下工作：** same-tenant relationship enforcement；在 CI 內完整的雙租
戶對抗性測試矩陣；待強制機制設計定案後，完成 tenant-isolation ADR。

第 5 階段才是隔離真正變難的地方：向量資料庫是比關聯式資料庫更弱的系統，其中不少
是在搜尋**之後**才過濾——而搜尋後過濾意味著另一個租戶的 chunk 早已被檢索出來。計
劃中的規則是：租戶過濾必須在搜尋**之前**套用，而且每一個被檢索出來的 chunk 在進
入 prompt 之前要再驗證一次——與第 3、4 層同樣的縱深防禦思路，套用在保證更少的系統
上。**LLM 永遠不是安全邊界**：每一個隔離決定都在程式碼中做出，在模型之前或之後，
永不由模型本身做。

## 9. 決策記錄索引

| ADR | 決策 |
|---|---|
| [0001](adr/0001-no-repository-abstraction.md) | 不在 EF Core 之上加 repository 抽象 |
| [0002](adr/0002-jwt-bearer-authentication.md) | 以 ASP.NET Core Identity 為基礎的 JWT bearer 認證 |
| [0003](adr/0003-blob-storage-user-delegation-sas.md) | 以 user-delegation SAS 存取檔案，不經 API 代理 |
| [0004](adr/0004-shared-database-shared-schema-tenancy.md) | 共用 database、共用 schema、`TenantId` 區分欄位 |

每份 ADR 記錄了決定了什麼、考慮過什麼替代方案、以及這個決定的代價——都是在做決定
的當下寫的，趁推理還新鮮。
