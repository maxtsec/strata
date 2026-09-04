// One physical test database is shared by every integration test in this
// assembly (see IntegrationTestFixture), so tests cannot safely run in
// parallel against it — this disables xUnit's default parallel execution
// across the whole assembly, not just within one collection.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
