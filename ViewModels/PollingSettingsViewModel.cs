using System.Collections.ObjectModel;
using EveCortex.Models;
using EveCortex.Services;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class CharacterOption(long id, string name)
{
    public long   Id   { get; } = id;
    public string Name { get; } = name;

    public override string ToString() => Name;
}

public class PollingSettingsViewModel : ReactiveObject
{

    private readonly AppPreferencesService _prefs;
    private bool _loading;

    public ObservableCollection<CharacterOption> StructureNameChars { get; } = [];

    private CharacterOption? _selectedStructureNameChar;
    public CharacterOption? SelectedStructureNameChar
    {
        get => _selectedStructureNameChar;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedStructureNameChar, value);
            if (!_loading) _ = SaveStructureNameCharAsync();
        }
    }

    public PollingSettingsViewModel(AppPreferencesService prefs)
    {
        _prefs = prefs;
    }

    public Task LoadAsync(IEnumerable<Character> characters)
    {
        _loading = true;
        try
        {
            StructureNameChars.Clear();
            StructureNameChars.Add(new CharacterOption(0, "(none — try all)"));
            foreach (var ch in characters)
                StructureNameChars.Add(new CharacterOption(ch.Id, ch.Name));

            var savedId = _prefs.GetLong(AppPreferencesService.StructureNameCharKey, 0);
            _selectedStructureNameChar = StructureNameChars.FirstOrDefault(c => c.Id == savedId)
                                         ?? StructureNameChars[0];
            this.RaisePropertyChanged(nameof(SelectedStructureNameChar));
        }
        finally
        {
            _loading = false;
        }
        return Task.CompletedTask;
    }

    private Task SaveStructureNameCharAsync()
    {
        var charId = _selectedStructureNameChar?.Id ?? 0;
        return _prefs.SetLongAsync(AppPreferencesService.StructureNameCharKey, charId == 0 ? null : charId);
    }
}
