using System.Windows;
using System.Windows.Controls;
using RdpManager.App.ViewModels;

namespace RdpManager.App.Views;

public partial class ListPageView : UserControl
{
    public ListPageView()
    {
        InitializeComponent();
        // Cards per row follow the width of the list.
        List.SizeChanged += (_, e) => { if (DataContext is ListPageViewModel p && e.WidthChanged) p.ViewportWidth = Math.Min(e.NewSize.Width, 1504); };
        DataContextChanged += (_, _) => { if (DataContext is ListPageViewModel p && List.ActualWidth > 0) p.ViewportWidth = Math.Min(List.ActualWidth, 1504); };
    }
}
