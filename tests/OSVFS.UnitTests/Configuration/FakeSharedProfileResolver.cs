using OSVFS.Configuration;

namespace OSVFS.UnitTests.Configuration;

/// <summary>
/// In-memory <see cref="ISharedProfileResolver"/> stub: returns the configured
/// <see cref="Result"/> on every call (or null when unset) and records what was
/// asked for so tests can assert that the SDK chain was — or was not — consulted.
/// </summary>
internal sealed class FakeSharedProfileResolver : ISharedProfileResolver
{
    /// <summary>Number of <see cref="Resolve"/> invocations observed.</summary>
    public int Calls { get; private set; }

    /// <summary>Most recent profile name passed to <see cref="Resolve"/>.</summary>
    public string? LastProfileName { get; private set; }

    /// <summary>Resolution to return; null simulates a profile-not-found miss.</summary>
    public SharedProfileResolution? Result { get; set; }

    /// <inheritdoc/>
    public SharedProfileResolution? Resolve(string profileName)
    {
        Calls++;
        LastProfileName = profileName;
        return Result;
    }
}
