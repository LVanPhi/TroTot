using System.Collections.Concurrent;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Tests;

/// <summary>Test double for feature tests that should not depend on Garage (T2.2+ swap it in via ConfigureTestServices).</summary>
public sealed class InMemoryObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<(string Bucket, string Key), (byte[] Content, string ContentType)> _objects = new();

    public IReadOnlyCollection<(string Bucket, string Key)> Keys => _objects.Keys.ToList();

    public Task PutAsync(string bucket, string key, byte[] content, string contentType, CancellationToken ct)
    {
        _objects[(bucket, key)] = (content.ToArray(), contentType);
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string bucket, string key, CancellationToken ct) =>
        Task.FromResult(_objects.TryGetValue((bucket, key), out var o) ? o.Content.ToArray() : null);

    public Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct) =>
        Task.FromResult(_objects.ContainsKey((bucket, key)));

    public Task DeleteAsync(string bucket, string key, CancellationToken ct)
    {
        _objects.TryRemove((bucket, key), out _);
        return Task.CompletedTask;
    }

    public string? ContentTypeOf(string bucket, string key) =>
        _objects.TryGetValue((bucket, key), out var o) ? o.ContentType : null;
}
