using System.Collections.Concurrent;

namespace Accessibility.Infrastructure.Processing;

public sealed class ProcessingCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public Registration Register(Guid jobId)
    {
        var source = new CancellationTokenSource();
        if (!_sources.TryAdd(jobId, source))
        {
            source.Dispose();
            throw new InvalidOperationException("The processing job is already running.");
        }
        return new Registration(jobId, source, this);
    }

    public void Cancel(Guid jobId)
    {
        if (_sources.TryGetValue(jobId, out var source)) source.Cancel();
    }

    public sealed class Registration : IDisposable
    {
        private readonly Guid _jobId;
        private readonly CancellationTokenSource _source;
        private readonly ProcessingCancellationRegistry _owner;

        internal Registration(Guid jobId, CancellationTokenSource source, ProcessingCancellationRegistry owner)
        {
            _jobId = jobId;
            _source = source;
            _owner = owner;
        }

        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            _owner._sources.TryRemove(_jobId, out _);
            _source.Dispose();
        }
    }
}
