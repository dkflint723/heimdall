using System.Runtime.CompilerServices;

// The same trade-off Heimdall.Windows/AssemblyInfo.cs weighs, reached the same
// way and worth restating rather than assumed from precedent.
//
// Everything else in Heimdall.Ui.Tests goes through the public surface —
// ThemeApplier, SettingsViewModel, the drawn icon sets — which is the right
// default. The exception is Program.InstanceMutexName, and it is an exception
// because of what that constant IS: half of a contract with the Windows
// installer, whose other half lives in packaging/heimdall.iss. Nothing observes
// the two agreeing at runtime, and nothing fails visibly when they stop —
// setup simply loses the ability to notice a running Heimdall, months before
// anyone upgrades with the app open and finds a half-written executable.
//
// The alternatives were making Program public, which puts an entry point on the
// API surface to satisfy a test, or copying the literal into the test, which
// would drift in precisely the same way and prove nothing. This is the smaller
// cost.
[assembly: InternalsVisibleTo("Heimdall.Ui.Tests")]
