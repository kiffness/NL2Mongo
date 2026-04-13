namespace NL2Mongo.Api.Records;

public record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);