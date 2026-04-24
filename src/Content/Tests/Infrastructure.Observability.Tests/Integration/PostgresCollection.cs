using Xunit;

namespace Infrastructure.Observability.Tests.Integration;

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
