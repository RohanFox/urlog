using Urlog.Core.Auth;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Auth;

/// <summary>
/// <see cref="PasswordHasher"/> behind a semaphore: at most <see cref="AuthOptions.MaxConcurrentHashes"/> Argon2
/// computations at once (64 MiB each), whatever the number of client addresses hitting <c>/login</c>.
/// </summary>
public sealed class PasswordService(IOptions<UrlogOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.Value.Auth.MaxConcurrentHashes));

    /// <summary>
    /// Called after every completed verification with the Argon2 parameters of the hash it ran against (null when the
    /// stored text was not a hash and nothing was computed). The login tests count through it that an unknown name
    /// costs one verification at the pinned parameters, exactly as a wrong password does.
    /// </summary>
    internal Action<Argon2Parameters?>? Verified { get; set; }

    public async Task<bool> VerifyAsync(string encoded, string password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var matches = await PasswordHasher.VerifyAsync(encoded, password).ConfigureAwait(false);
            Verified?.Invoke(PasswordHasher.TryParse(encoded, out var parsed) ? parsed.Parameters : null);
            return matches;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> HashAsync(string password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PasswordHasher.HashAsync(password).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
