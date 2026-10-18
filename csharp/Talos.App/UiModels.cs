using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Talos.Core;

namespace Talos.App;

public sealed record Choice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class RoomEditor : INotifyPropertyChanged
{
    public int Index { get; }
    public string Number => (Index + 1).ToString("00");
    private string kind = "unknown", species = "", eventKind = "", note = "待人工核对";
    private int? tier, nestDue;
    private bool destination, hasEvent, seen, ivory, scratch;
    public string Kind { get => kind; set => Set(ref kind, value); }
    public string Species { get => species; set => Set(ref species, value); }
    public int? Tier { get => tier; set => Set(ref tier, value); }
    public int? NestDue { get => nestDue; set => Set(ref nestDue, value); }
    public bool Destination { get => destination; set => Set(ref destination, value); }
    public bool Event { get => hasEvent; set => Set(ref hasEvent, value); }
    public bool Seen { get => seen; set => Set(ref seen, value); }
    public string EventKind { get => eventKind; set => Set(ref eventKind, value); }
    public bool Ivory { get => ivory; set => Set(ref ivory, value); }
    public bool Scratch { get => scratch; set => Set(ref scratch, value); }
    public string Note { get => note; set => Set(ref note, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public RoomEditor(int index) { Index = index; kind = index == 0 ? "office" : "unknown"; }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public RoomState ToState() => new()
    {
        Index = Index, Kind = Kind, Species = string.IsNullOrEmpty(Species) ? null : Species,
        Tier = Kind == "inmate" ? Tier : null, NestDue = Kind == "nest" ? NestDue : null,
        Event = Event, Seen = Seen, EventKind = Event && !string.IsNullOrEmpty(EventKind) ? EventKind : null, Ivory = Ivory
    };
}
