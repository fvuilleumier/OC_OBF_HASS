using CommunityToolkit.Mvvm.ComponentModel;
using MHassnage.Core.Model;

namespace MHassnage.Wpf.Controller;

/// <summary>Opération courante ; les panels s'abonnent à son changement.</summary>
public sealed partial class NavigationController : ObservableObject
{
    [ObservableProperty]
    private Operation _current = Operation.Status;
}
