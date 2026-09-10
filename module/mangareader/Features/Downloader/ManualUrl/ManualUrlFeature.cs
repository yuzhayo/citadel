namespace Module.Mangareader.Features.Downloader.ManualUrl;

/// <summary>
/// Coordinates one explicit manual URL request. Provider recognition is kept
/// in ordered probes; this feature does not know provider routes or parse HTML.
/// </summary>
public sealed class ManualUrlFeature
{
    private readonly IReadOnlyList<IManualUrlProbe> _probes;
    private readonly object _gate = new();
    private ManualUrlState _state = new();
    private int _generation;

    public ManualUrlFeature(IReadOnlyList<IManualUrlProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToArray();
        if (_probes.Any(probe => probe is null))
        {
            throw new ArgumentException("Manual URL probes cannot contain null.", nameof(probes));
        }
    }

    public event EventHandler? StateChanged;

    public ManualUrlState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public async Task LoadAsync(string? input, CancellationToken cancellationToken)
    {
        var text = input?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url)
            || url.Scheme is not ("https" or "http"))
        {
            NextGeneration();
            Mutate(state => state with
            {
                UrlText = text,
                IsBusy = false,
                Resolution = null,
                StatusMessage = null,
                ErrorMessage = "Masukkan URL http atau https yang valid.",
            });
            return;
        }

        var generation = NextGeneration();
        Mutate(state => state with
        {
            UrlText = text,
            IsBusy = true,
            Resolution = null,
            StatusMessage = "Memeriksa URL…",
            ErrorMessage = null,
        });

        try
        {
            foreach (var probe in _probes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await probe.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
                if (generation != CurrentGeneration()) return;

                switch (result)
                {
                    case ManualUrlProbeResult.NoMatchResult:
                        continue;
                    case ManualUrlProbeResult.ResolvedResult resolved:
                        var resolution = ManualUrlResolution.From(probe.SourceId, resolved.Title);
                        Mutate(state => state with
                        {
                            IsBusy = false,
                            Resolution = resolution,
                            StatusMessage = $"URL dikenali sebagai {probe.SourceId}.",
                            ErrorMessage = null,
                        });
                        return;
                    case ManualUrlProbeResult.InvalidResult invalid:
                        Mutate(state => state with
                        {
                            IsBusy = false,
                            Resolution = null,
                            StatusMessage = null,
                            ErrorMessage = invalid.Message,
                        });
                        return;
                    default:
                        throw new InvalidOperationException("Unknown manual URL probe result.");
                }
            }

            Mutate(state => state with
            {
                IsBusy = false,
                Resolution = null,
                StatusMessage = null,
                ErrorMessage = "URL belum didukung oleh provider yang tersedia.",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation != CurrentGeneration()) return;
            Mutate(state => state with { IsBusy = false, StatusMessage = null });
        }
        catch (Exception exception)
        {
            if (generation != CurrentGeneration()) return;
            Mutate(state => state with
            {
                IsBusy = false,
                Resolution = null,
                StatusMessage = null,
                ErrorMessage = exception.Message,
            });
        }
    }

    public void Clear()
    {
        NextGeneration();
        Mutate(_ => new ManualUrlState());
    }

    /// <summary>Allows the route host to return a downstream detail error to the Manual screen.</summary>
    public void ReportError(string message) =>
        Mutate(state => state with
        {
            IsBusy = false,
            Resolution = null,
            StatusMessage = null,
            ErrorMessage = string.IsNullOrWhiteSpace(message) ? "URL tidak dapat dibuka." : message,
        });

    private int NextGeneration() => Interlocked.Increment(ref _generation);

    private int CurrentGeneration() => Volatile.Read(ref _generation);

    private void Mutate(Func<ManualUrlState, ManualUrlState> update)
    {
        lock (_gate)
        {
            _state = update(_state);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
