using System.IO;
using System.Windows.Controls;
using Citadel.Shell;
using Citadel.Ui.Controls;

namespace Citadel.Uia;

public sealed class SidebarGroupingTests
{
    [Fact]
    public void ShellComposesGroupsAndReturnsDeletedMembersToUngrouped()
    {
        Sta.Run(() =>
        {
            using var fixture = new ShellFixture();
            var window = fixture.CreateWindow();
            fixture.Gate.Register(Fake.Descriptor("ftf", "FTF", order: 30));
            fixture.Gate.Register(Fake.Descriptor("proxy", "Proxy", order: 40));
            fixture.Main.Pump();

            var id = fixture.SettingHost.CreateSidebarGroup("Tools");
            fixture.SettingHost.SetSidebarGroupMembership(id, "ftf", included: true);
            fixture.SettingHost.SetSidebarGroupMembership(id, "proxy", included: true);

            Assert.Equal(
                ["sidebar-group:" + id, "ftf", "proxy"],
                window.SidebarControl.MainEntries.Select(entry => entry.Route));

            window.ShowInTaskbar = false;
            window.Show();
            window.UpdateLayout();
            var list = Assert.IsType<ListBox>(
                window.SidebarControl.Template.FindName(Sidebar.NavListPart, window.SidebarControl));
            list.SelectedItem = window.SidebarControl.MainEntries[0];
            Assert.Equal(["sidebar-group:" + id], window.SidebarControl.MainEntries.Select(entry => entry.Route));

            fixture.SettingHost.DeleteSidebarGroup(id);
            Assert.Equal(["ftf", "proxy"], window.SidebarControl.MainEntries.Select(entry => entry.Route));
            window.Close();
        });
    }

    [Fact]
    public void MembershipIsExclusiveAndPersistsAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "citadel-sidebar-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "groups.json");
        try
        {
            var store = new SidebarGroupingStore(path);
            var tools = store.Create("Tools");
            var media = store.Create("Media");
            store.SetMembership(tools, "proxy", included: true);
            store.SetMembership(media, "proxy", included: true);
            store.SetExpanded(media, expanded: false);

            var reloaded = new SidebarGroupingStore(path);
            var groups = reloaded.Snapshot();

            Assert.DoesNotContain("proxy", groups.Single(group => group.Id == tools).Routes);
            var persisted = groups.Single(group => group.Id == media);
            Assert.Equal(["proxy"], persisted.Routes);
            Assert.False(persisted.Expanded);

            reloaded.Delete(media);
            Assert.DoesNotContain(reloaded.Snapshot(), group => group.Id == media);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
