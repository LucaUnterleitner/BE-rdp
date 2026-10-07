using System.Windows;
using System.Windows.Shell;
using RdpManager.Infrastructure.Logging;

namespace RdpManager.App.Services;

/// <summary>Recent systems in the taskbar jump list; each entry starts the app with --connect=&lt;id&gt;.</summary>
public static class JumpListService
{
    public static void Update(AppController controller)
    {
        try
        {
            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            foreach (var c in controller.RecentConnections(6))
            {
                list.JumpItems.Add(new JumpTask
                {
                    Title = c.Name,
                    Description = $"Connect to {c.Host}",
                    ApplicationPath = Environment.ProcessPath,
                    Arguments = $"--connect={c.Id}",
                    IconResourcePath = Environment.ProcessPath,
                    IconResourceIndex = 0,
                    CustomCategory = "Recent systems",
                });
            }
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("Jump list could not be updated", e); // jump lists are optional
        }
    }
}
