using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Diagnostics;
using System.Windows.Threading;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Hosts a real WPF <see cref="Application"/> on a dedicated STA thread so the views can be loaded
/// and laid out in a test.
///
/// One Application per process and it belongs to the thread that created it, so everything runs on
/// this single thread and the fixture is shared by the whole class.
/// </summary>
/// <summary>
/// Shares the one Application across every class that needs it. A second WpfFixture instance would
/// try to construct a second Application, which the runtime refuses - so classes join this
/// collection rather than each taking their own class fixture.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture>
{
    public const string Name = "Wpf";
}

public sealed class WpfFixture : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher _dispatcher = null!;

    public WpfFixture()
    {
        using var ready = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            // InitializeComponent is what loads App.xaml - and therefore Themes/DarkTheme.xaml and
            // Themes/Logo.xaml - into Application.Resources. The generated Main calls it after the
            // constructor, so constructing App alone leaves the resources empty and every
            // {StaticResource} in every view fails. OnStartup only runs from Run(), which is not
            // called here, so no splash window appears.
            var app = new App();
            app.InitializeComponent();

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-test-ui"
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>The fixture's dispatcher, for tests that gate or queue against it directly.</summary>
    public Dispatcher Dispatcher => _dispatcher;

    /// <summary>Runs on the UI thread and rethrows anything it threw, with its stack intact.</summary>
    public void Invoke(Action action)
    {
        Exception? captured = null;

        _dispatcher.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });

        if (captured != null) ExceptionDispatchInfo.Capture(captured).Throw();
    }

    /// <summary>
    /// Deliberately does NOT shut the dispatcher down.
    ///
    /// It used to call InvokeShutdown and join with a five-second timeout. When the join timed out
    /// - which it started doing once there was enough WPF work queued - the thread was left
    /// running managed code while the runtime tore down around it, and the test host died with
    /// "Attempt to execute managed code after the .NET runtime thread state has been destroyed".
    /// That aborted the whole run mid-way, after every test in it had passed.
    ///
    /// The thread is IsBackground, so it cannot keep the process alive; letting the process end it
    /// removes the race entirely. There is exactly one of these per run and it owns the only
    /// Application, so nothing is leaked that outlives the process.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Collects WPF's own binding failures.
///
/// A broken binding does not throw - WPF writes it to a trace source and carries on with an empty
/// value. That is why a wrong RelativeSource renders a button that simply does nothing when
/// clicked, with no error anywhere. Listening to the trace source is the only way to see them.
/// </summary>
public sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors => _errors;

    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) _errors.Add(message);
    }

    /// <summary>Starts listening, and stops when disposed. Binding failures trace at Warning.</summary>
    public static BindingErrorListener Attach()
    {
        var listener = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        return listener;
    }

    public void Detach() => PresentationTraceSources.DataBindingSource.Listeners.Remove(this);

    public void AssertNone(string what)
    {
        if (_errors.Count == 0) return;

        Assert.Fail(
            $"{what} produced {_errors.Count} binding failure(s):{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", _errors));
    }
}
