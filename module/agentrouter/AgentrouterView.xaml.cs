using System.Windows.Controls;
using Citadel.Core.Rpl;
using Module.Agentrouter.Features.Claim;
using Module.Agentrouter.Features.Proxy;
using Module.Agentrouter.Features.Shortcuts;
using Module.Agentrouter.SharedLogic;

namespace Module.Agentrouter;

/// <summary>
/// Agentrouter's composition root. It assembles the module's own stores, hands
/// them to the feature views, and routes tab selection to the feature that
/// cares. Which profiles become shortcuts, and how the committed pool is
/// presented, belong to the feature folders — this class stays a router.
/// </summary>
public partial class AgentrouterView : UserControl
{
    private readonly ShortcutsView _shortcutsView;
    private readonly ProxyView _proxyView;
    private readonly IAgentRouterClaimService _claims;
    private bool _disposed;

    public AgentrouterView(Lifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        InitializeComponent();

        // Module-level stores: shared mechanisms, carrying no feature policy.
        var shortcuts = new ShortcutCatalog();
        var pool = new AgentProxyPool();

        // The claim flow runs in this citizen's own pyhost process. The parent
        // creates it and owns its lifetime, exactly like the stores above.
        _claims = new AgentRouterClaimFeature();

        // Each feature view loads its own data, so the parent never has to
        // know what "ready" means for either of them.
        _shortcutsView = new ShortcutsView(shortcuts, pool, _claims);
        _proxyView = new ProxyView(pool);

        ShortcutsHost.Content = _shortcutsView;
        ProxyHost.Content = _proxyView;

        lifetime.Add(DisposeView);
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _shortcutsView is null || _proxyView is null
            || !ReferenceEquals(e.Source, WorkspaceTabs))
        {
            return;
        }

        // The pool is written by the Proxy citizen; re-read it whenever its
        // tab comes forward so the table never shows a superseded commit.
        if (ReferenceEquals(WorkspaceTabs.SelectedItem, ProxyPanel))
        {
            _proxyView.Reload();
        }
        else if (ReferenceEquals(WorkspaceTabs.SelectedItem, LauncherPanel))
        {
            _shortcutsView.Refresh();
        }
    }

    private void DisposeView()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // This screen created both feature views, so it owns their disposal.
        _shortcutsView.Dispose();
        _proxyView.Dispose();
        _claims.Dispose();
    }
}
