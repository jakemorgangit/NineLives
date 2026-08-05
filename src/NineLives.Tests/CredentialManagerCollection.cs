using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Serialises the test classes that read and write Windows Credential Manager.
///
/// xUnit runs different classes in parallel, and four classes here hammer the same per-user
/// credential store at once. Every key is uniquely namespaced so they cannot collide logically,
/// but the store itself intermittently failed a read straight after a successful write under that
/// load - twice, in two different classes, neither reproducible on a rerun.
///
/// A flaky suite is worse than a slow one anywhere, and especially here: these are the tests that
/// prove secrets are stored and retrieved correctly, so they are precisely the ones nobody should
/// learn to re-run and ignore. They lose a little parallelism and gain being trustworthy.
/// </summary>
[CollectionDefinition("CredentialManager", DisableParallelization = true)]
public sealed class CredentialManagerCollection;
