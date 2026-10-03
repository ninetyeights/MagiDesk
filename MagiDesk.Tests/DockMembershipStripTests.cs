using System.Windows;
using System.Windows.Controls;
using MagiDesk.Controls;
using MagiDesk.Config;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Tests;

internal static class DockMembershipStripTests
{
    internal static void ClickEvents()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var collection = new DockCollection();
                var column = new BrowserDockGroup();
                var target = new DockProjectTarget(collection, column);
                var strip = new DockMembershipStrip { Memberships = new[] { target } };
                int managed = 0, removed = 0;
                strip.ManageRequested += (sender, e) =>
                {
                    if (sender != strip || e.RoutedEvent != DockMembershipStrip.ManageRequestedEvent) throw new Exception("Wrong manage event");
                    e.Handled = true;
                    managed++;
                };
                strip.RemoveRequested += (_, e) =>
                {
                    if (strip.RequestedTarget != target || e.RoutedEvent != DockMembershipStrip.RemoveRequestedEvent) throw new Exception("Wrong removal target or event");
                    e.Handled = true;
                    removed++;
                };
                var row = (StackPanel)strip.Content;
                ((Button)row.Children[row.Children.Count - 1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var tag = row.Children.OfType<Border>().Single();
                ((StackPanel)tag.Child).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (managed != 1 || removed != 1) throw new Exception("Click must dispatch each request exactly once");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Membership event test timed out");
        if (failure is not null) throw new InvalidOperationException("Membership click regression", failure);
    }
}
