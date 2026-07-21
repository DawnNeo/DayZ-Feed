using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DayZLauncher.Models
{
    /// <summary>
    /// Minimal INotifyPropertyChanged base for the data models.
    ///
    /// The models used to be plain POCOs, which meant that anything the app changed on a Mod or
    /// Server after it was bound (update badges, favourite stars, live ping, enabled state) never
    /// reached the UI - the value changed but the screen kept showing the old one. Anything that
    /// the views bind to now lives on properties that raise PropertyChanged.
    /// </summary>
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
