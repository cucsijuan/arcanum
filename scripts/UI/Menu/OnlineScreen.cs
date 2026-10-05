// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Net.Protocol;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Online play by direct connection: host a game (others join with this device's address) or join one, then wait in
/// the lobby until the host starts. The host fills free seats with the computer.
/// </summary>
public partial class OnlineScreen : Control
{
    private readonly List<DeckInfo> _decks = new();
    private readonly VBoxContainer _root = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly Label _status = MenuKit.Hint("");
    private LineEdit _name = null!;
    private OptionButton _deck = null!;
    private static OnlineService Online => App.Instance.Online;

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        MenuKit.AddHeader(this, "Online", () =>
        {
            Online.Leave();
            App.Instance.GoTo(App.MainMenuScene);
        });

        var scroll = new ScrollContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 140, OffsetTop = 100, OffsetRight = -140, OffsetBottom = -60,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        _root.AddThemeConstantOverride("separation", 20);
        scroll.AddChild(_root);

        _status.AnchorTop = 1; _status.AnchorBottom = 1; _status.AnchorRight = 1;
        _status.OffsetLeft = 140; _status.OffsetTop = -52; _status.OffsetRight = -140;
        AddChild(_status);

        var loading = MenuKit.Hint(App.Instance.ContentStatus);
        _root.AddChild(loading);
        await App.Instance.ContentReady;
        loading.QueueFree();
        _decks.AddRange(App.Instance.Decks.List(App.Instance.Module));

        Online.Changed += Rebuild;
        Online.Status += ShowStatus;
        Rebuild();
        AutoStart();
    }

    /// <summary>
    /// Quick testing without the menus: ARCANUM_ONLINE_HOST=port[,players] hosts with the first deck (the computer takes
    /// seats left free after ARCANUM_ONLINE_WAIT seconds, default 20) and starts when the lobby is complete
    /// (ARCANUM_ONLINE_EVENT=draft|sealed hosts an event with the first booster source instead);
    /// ARCANUM_ONLINE_JOIN=address joins; ARCANUM_ONLINE_REJOIN=1 gets back into the last joined game.
    /// </summary>
    private void AutoStart()
    {
        var host = OS.GetEnvironment("ARCANUM_ONLINE_HOST");
        var join = OS.GetEnvironment("ARCANUM_ONLINE_JOIN");
        if (OS.GetEnvironment("ARCANUM_ONLINE_REJOIN") == "1")
        {
            Online.Status += text => GD.Print($"ONLINE {text}");
            Online.Rejoin("Guest");
            return;
        }
        if (_decks.Count == 0 || (host.Length == 0 && join.Length == 0)) return;
        Online.Status += text => GD.Print($"ONLINE {text}");
        Online.Changed += () => GD.Print($"ONLINE lobby: {Online.HostedLobby?.StartProblem ?? Online.Lobby?.State?.Seats.Count.ToString() ?? "-"}");
        int deckIndex = int.TryParse(OS.GetEnvironment("ARCANUM_ONLINE_DECK"), out int d) ? d % _decks.Count : 0;
        var deck = _decks[deckIndex];
        if (join.Length > 0)
        {
            Online.Join($"Guest {OS.GetProcessId() % 100}", join, deck);
            return;
        }
        var parts = host.Split(',');
        int port = int.TryParse(parts[0], out int p) ? p : OnlineService.DefaultPort;
        int players = parts.Length > 1 && int.TryParse(parts[1], out int n) ? Math.Clamp(n, 2, 4) : 2;
        var eventMode = OS.GetEnvironment("ARCANUM_ONLINE_EVENT");
        if (eventMode.Length > 0 && App.Instance.Limited.Sources().FirstOrDefault() is { } eventSource)
            Online.HostEvent("Host", eventMode == "sealed" ? Arcanum.Data.Limited.LimitedMode.Sealed : Arcanum.Data.Limited.LimitedMode.Draft,
                eventSource, 1, players, port);
        else Online.Host("Host", App.Instance.FormatById(deck.FormatId), players, port, deck);
        int wait = int.TryParse(OS.GetEnvironment("ARCANUM_ONLINE_WAIT"), out int w) ? w : 20;
        GetTree().CreateTimer(wait).Timeout += () =>
        {
            if (Online.HostedLobby is not { Game: null } lobby) return;
            for (int i = 1; i < lobby.Settings.Seats; i++)
                if (lobby.State.Seats[i].Kind == LobbySeatKind.Open)
                {
                    var (list, _) = App.Instance.Decks.Load(_decks[(deckIndex + i) % _decks.Count]);
                    if (lobby.Settings.Event is not null) lobby.SetComputer(i, $"Computer {i}", "", "");
                    else lobby.SetComputer(i, $"Computer {i}", _decks[(deckIndex + i) % _decks.Count].Name, list.Export());
                }
        };
        Online.Changed += () =>
        {
            if (Online.HostedLobby is { Game: null, Event: null, StartProblem: null }) Online.StartGame();
        };
    }

    public override void _ExitTree()
    {
        Online.Changed -= Rebuild;
        Online.Status -= ShowStatus;
    }

    private void ShowStatus(string text) => _status.Text = text;

    private void Rebuild()
    {
        if (!IsInsideTree()) return;
        foreach (var child in _root.GetChildren()) child.QueueFree();
        if (Online.Lobby is { } lobby) BuildLobby(lobby);
        else BuildStart();
    }

    private string PlayerName => _name?.Text is { Length: > 0 } n ? n : Settings.Current.PlayerNames.ElementAtOrDefault(0) ?? "Player";

    private DeckInfo? SelectedDeck => _deck is not null && _deck.Selected >= 0 && _deck.Selected < _decks.Count ? _decks[_deck.Selected] : null;

    private OptionButton DeckPicker(int selected)
    {
        var picker = MenuKit.Options(_decks.Select(d => d.Name), Math.Clamp(selected, 0, Math.Max(0, _decks.Count - 1)));
        picker.CustomMinimumSize = new Vector2(360, 0);
        return picker;
    }

    // ------------------------------------------------------------------ host or join

    private void BuildStart()
    {
        if (_decks.Count == 0)
        {
            _root.AddChild(MenuKit.Hint("You need a deck to play online: build one in Decks, or install the card content."));
            return;
        }

        var you = new VBoxContainer();
        you.AddThemeConstantOverride("separation", 10);
        you.AddChild(MenuKit.SectionTitle("You"));
        _name = MenuKit.TextField(Settings.Current.PlayerNames.ElementAtOrDefault(0) ?? "Player 1", "Name");
        you.AddChild(MenuKit.Row("Name", _name, 90));
        _deck = DeckPicker(_lastDeck);
        _deck.ItemSelected += i => _lastDeck = (int)i;
        you.AddChild(MenuKit.Row("Deck", _deck, 90));
        _root.AddChild(MenuKit.Card(you));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 20);

        var host = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        host.AddThemeConstantOverride("separation", 10);
        host.AddChild(MenuKit.SectionTitle("Host a game"));
        host.AddChild(MenuKit.Hint("Other players join with your address. On another network, your router must let them in (it's tried automatically)."));
        var kind = MenuKit.Options(new[] { "Game (bring a deck)", "Draft", "Sealed" }, _kind);
        host.AddChild(MenuKit.Row("Play", kind, 90));
        bool limited = _kind > 0;
        var formats = App.Instance.Formats;
        var format = MenuKit.Options(formats.Select(f => f.Name), Math.Max(0, formats.FindIndex(f => f.Id == SelectedDeck?.FormatId)));
        var sources = App.Instance.Limited.Sources();
        var source = MenuKit.Options(sources.Select(x => x.Name), Math.Min(_sourceIndex, Math.Max(0, sources.Count - 1)));
        source.ItemSelected += i => _sourceIndex = (int)i;
        var bestOf = MenuKit.Options(new[] { "Best of one", "Best of three" }, _bestOfIndex);
        bestOf.ItemSelected += i => _bestOfIndex = (int)i;
        if (limited)
        {
            host.AddChild(MenuKit.Row("Boosters", source, 90));
            host.AddChild(MenuKit.Row("Matches", bestOf, 90));
        }
        else host.AddChild(MenuKit.Row("Format", format, 90));
        var players = MenuKit.Options(Enumerable.Range(2, limited ? 7 : 3).Select(n => $"{n} players"), Math.Min(_playersIndex, limited ? 6 : 2));
        players.ItemSelected += i => _playersIndex = (int)i;
        host.AddChild(MenuKit.Row("Players", players, 90));
        kind.ItemSelected += i =>
        {
            _kind = (int)i;
            Rebuild();
        };
        if (limited) host.AddChild(MenuKit.Hint("Free seats can be given to the computer in the lobby. Everyone gets a time limit for each pick and for building their deck."));
        var port = MenuKit.TextField(OnlineService.DefaultPort.ToString(), "Port");
        host.AddChild(MenuKit.Row("Port", port, 90));
        var hostButton = BoardStyle.MakePrimaryButton("Host", 20);
        hostButton.CustomMinimumSize = new Vector2(200, 50);
        hostButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        hostButton.Pressed += () =>
        {
            if (SelectedDeck is not { } deck) return;
            if (!int.TryParse(port.Text, out int p) || p is < 1 or > 65535)
            {
                ShowStatus("The port must be a number from 1 to 65535.");
                return;
            }
            SaveName();
            if (!limited) Online.Host(PlayerName, formats[format.Selected], players.Selected + 2, p, deck);
            else if (sources.Count == 0) ShowStatus("No set with boosters or cube is available.");
            else Online.HostEvent(PlayerName, _kind == 1 ? Arcanum.Data.Limited.LimitedMode.Draft : Arcanum.Data.Limited.LimitedMode.Sealed,
                sources[source.Selected], bestOf.Selected == 0 ? 1 : 3, players.Selected + 2, p);
        };
        host.AddChild(hostButton);
        if (OnlineService.HasSavedEvent)
        {
            var resumeEvent = BoardStyle.MakeButton("Resume interrupted event", 16);
            resumeEvent.TooltipText = "Host again the draft or sealed event that was running when this device stopped hosting.";
            resumeEvent.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            resumeEvent.Pressed += () => Online.ResumeSavedEvent(PlayerName);
            host.AddChild(resumeEvent);
        }
        if (OnlineService.HasSavedGame)
        {
            var resume = BoardStyle.MakeButton("Resume interrupted game", 16);
            resume.TooltipText = "Host again the game that was running when this device stopped hosting; players get back in with their seats.";
            resume.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            resume.Pressed += () => Online.ResumeSavedGame();
            host.AddChild(resume);
        }
        var hostCard = MenuKit.Card(host);
        hostCard.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(hostCard);

        var join = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        join.AddThemeConstantOverride("separation", 10);
        join.AddChild(MenuKit.SectionTitle("Join a game"));
        join.AddChild(MenuKit.Hint("Enter the host's invite code, or their address."));
        var code = MenuKit.TextField("", "ABC-DEF");
        join.AddChild(MenuKit.Row("Invite code", code, 90));
        var codeButton = BoardStyle.MakeButton("Join with code", 16);
        codeButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        codeButton.Pressed += () =>
        {
            if (SelectedDeck is not { } deck) return;
            SaveName();
            Online.JoinByCode(PlayerName, code.Text, deck);
        };
        join.AddChild(codeButton);
        var address = MenuKit.TextField(Settings.Current.LastHostAddress, $"192.168.1.20:{OnlineService.DefaultPort}");
        join.AddChild(MenuKit.Row("Address", address, 90));
        var joinButton = BoardStyle.MakePrimaryButton("Join", 20);
        joinButton.CustomMinimumSize = new Vector2(200, 50);
        joinButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        joinButton.Pressed += () =>
        {
            if (SelectedDeck is not { } deck) return;
            SaveName();
            Settings.Current.LastHostAddress = address.Text.Trim();
            Settings.Save();
            Online.Join(PlayerName, address.Text, deck);
        };
        join.AddChild(joinButton);
        if (OnlineService.CanRejoin)
        {
            var rejoin = BoardStyle.MakeButton($"Get back into the game at {Settings.Current.LastHostAddress}", 16);
            rejoin.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            rejoin.Pressed += () => Online.Rejoin(PlayerName);
            join.AddChild(rejoin);
        }
        var joinCard = MenuKit.Card(join);
        joinCard.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(joinCard);

        _root.AddChild(row);
        _root.AddChild(BuildBrowser());
    }

    // ------------------------------------------------------------------ lobby browser

    private IReadOnlyList<Arcanum.Net.Services.LobbyListing>? _found;
    private bool _searching;

    /// <summary>Open lobbies the online services list (on the local network for now), each joinable with the chosen deck.</summary>
    private Control BuildBrowser()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        var title = MenuKit.SectionTitle("Open lobbies");
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        var refresh = BoardStyle.MakeButton(_searching ? "Searching…" : "Refresh", 16);
        refresh.Disabled = _searching;
        refresh.Pressed += SearchLobbies;
        header.AddChild(refresh);
        box.AddChild(header);
        box.AddChild(MenuKit.Hint($"Lobbies on: {Online.Services.Name}."));
        if (_found is null) box.AddChild(MenuKit.Hint(_searching ? "Looking for lobbies…" : "Press Refresh to look for lobbies."));
        else if (_found.Count == 0) box.AddChild(MenuKit.Hint("No open lobby was found."));
        else foreach (var listing in _found) box.AddChild(ListingRow(listing));
        return MenuKit.Card(box);
    }

    private Control ListingRow(Arcanum.Net.Services.LobbyListing listing)
    {
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 12);
        string what = listing.Event ?? (listing.Commander ? $"{listing.Format} (Commander)" : listing.Format);
        var label = BoardStyle.MakeLabel($"{listing.HostName} · {what} · {listing.OpenSeats} of {listing.Seats} seats free", 16);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(label);
        var join = BoardStyle.MakePrimaryButton("Join", 16);
        join.Pressed += () =>
        {
            if (SelectedDeck is not { } deck) return;
            SaveName();
            Online.JoinListing(PlayerName, listing, deck);
        };
        line.AddChild(join);
        return line;
    }

    private async void SearchLobbies()
    {
        if (_searching) return;
        if (_name is not null) SaveName();
        _searching = true;
        Rebuild();
        try { _found = await Online.SearchLobbiesAsync(); }
        catch (Exception e)
        {
            _found = Array.Empty<Arcanum.Net.Services.LobbyListing>();
            ShowStatus($"Couldn't look for lobbies: {e.Message}");
        }
        _searching = false;
        if (IsInsideTree()) Rebuild();
    }

    private static int _lastDeck, _kind, _sourceIndex, _bestOfIndex, _playersIndex;

    private void SaveName()
    {
        Settings.Current.PlayerNames[0] = PlayerName;
        Settings.Save();
    }

    // ------------------------------------------------------------------ lobby

    private void BuildLobby(Arcanum.Net.Lobby.LobbyClient lobby)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        var state = lobby.State;
        box.AddChild(MenuKit.SectionTitle(state is null ? "Joining…" : $"Lobby · {state.Event ?? state.Format}"));

        if (Online.IsHosting && Online.PublishedLobby is { } published)
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);
            line.AddChild(BoardStyle.MakeLabel($"Invite code: {Arcanum.Net.Services.InviteCode.Display(published.InviteCode)}", 18));
            var copy = BoardStyle.MakeButton("Copy", 13);
            copy.Pressed += () => DisplayServer.ClipboardSet(Arcanum.Net.Services.InviteCode.Display(published.InviteCode));
            line.AddChild(copy);
            box.AddChild(line);
            box.AddChild(MenuKit.Hint($"The lobby is listed in the lobby browser ({Online.Services.Name})."));
        }
        if (Online.IsHosting)
        {
            box.AddChild(MenuKit.Hint("Or give the other players one of these addresses:"));
            foreach (var address in Online.ShareAddresses)
            {
                var line = new HBoxContainer();
                line.AddThemeConstantOverride("separation", 10);
                line.AddChild(BoardStyle.MakeLabel(address, 16));
                var copy = BoardStyle.MakeButton("Copy", 13);
                string text = address.Split(' ')[0];
                copy.Pressed += () => DisplayServer.ClipboardSet(text);
                line.AddChild(copy);
                box.AddChild(line);
            }
        }

        if (state is not null)
        {
            for (int i = 0; i < state.Seats.Count; i++) box.AddChild(SeatRow(i, state.Seats[i], lobby.Seat == i));

            if (state.Event is null)
            {
                _deck = DeckPicker(_lastDeck);
                _deck.ItemSelected += i =>
                {
                    _lastDeck = (int)i;
                    if (SelectedDeck is { } deck) Online.SubmitDeck(deck);
                };
                box.AddChild(MenuKit.Row("Your deck", _deck, 120));
            }
            else box.AddChild(MenuKit.Hint("Decks come from the boosters opened in the event."));
        }

        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        actions.AddThemeConstantOverride("separation", 12);
        var leave = BoardStyle.MakeButton("Leave", 18);
        leave.CustomMinimumSize = new Vector2(140, 50);
        leave.Pressed += () =>
        {
            Online.Leave();
            Rebuild();
        };
        actions.AddChild(leave);
        if (Online.HostedLobby is { } hosted)
        {
            var problem = hosted.StartProblem;
            if (problem is not null) actions.AddChild(MenuKit.Hint(problem));
            if (hosted.State.Seats.Any(s => s.Kind == LobbySeatKind.Open))
            {
                var fill = BoardStyle.MakeButton("\U0001F916 Fill free seats with the computer", 16);
                fill.CustomMinimumSize = new Vector2(0, 50);
                fill.TooltipText = "The computer plays every seat nobody took (with its own decks, or its own boosters in an event)";
                fill.Pressed += Online.FillWithComputer;
                actions.AddChild(fill);
            }
            var start = BoardStyle.MakePrimaryButton("Start game", 20);
            start.CustomMinimumSize = new Vector2(220, 50);
            start.Disabled = problem is not null;
            start.Pressed += Online.StartGame;
            actions.AddChild(start);
        }
        else actions.AddChild(MenuKit.Hint("Waiting for the host to start…"));
        box.AddChild(actions);
        _root.AddChild(MenuKit.Card(box));
    }

    private Control SeatRow(int index, LobbySeat seat, bool isMe)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        string who = seat.Kind switch
        {
            LobbySeatKind.Open => "Free seat",
            LobbySeatKind.Computer => $"🤖 {seat.Name}",
            _ => seat.Name + (isMe ? " (you)" : "") + (seat.Connected ? "" : " · disconnected"),
        };
        var name = BoardStyle.MakeLabel($"{index + 1}. {who}", 17, seat.Kind == LobbySeatKind.Open ? BoardStyle.TextDim : BoardStyle.Text);
        name.CustomMinimumSize = new Vector2(320, 0);
        row.AddChild(name);
        if (seat.Kind != LobbySeatKind.Open)
        {
            var deck = seat.Problem is { } p ? $"{seat.Deck ?? "No deck"} — {p}" : seat.Deck ?? "";
            var label = MenuKit.Hint(deck);
            if (seat.Problem is not null) label.AddThemeColorOverride("font_color", BoardStyle.Attacking);
            row.AddChild(label);
        }
        else row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        if (Online.HostedLobby is { } hosted && index > 0)
        {
            var computerDeck = DeckPicker(index % Math.Max(1, _decks.Count));
            computerDeck.CustomMinimumSize = new Vector2(240, 0);
            computerDeck.TooltipText = "Deck for the computer";
            bool isEvent = hosted.Settings.Event is not null; // the computer drafts or opens its own cards
            var computer = BoardStyle.MakeButton(seat.Kind == LobbySeatKind.Computer && !isEvent ? "Change deck" : "Computer", 14);
            computer.TooltipText = "The computer plays this seat";
            computer.Pressed += () =>
            {
                string name = index == 1 && hosted.Settings.Seats == 2 ? "Computer" : $"Computer {index}";
                if (isEvent)
                {
                    hosted.SetComputer(index, name, "", "");
                    return;
                }
                var info = _decks[computerDeck.Selected];
                var (list, _) = App.Instance.Decks.Load(info);
                hosted.SetComputer(index, name, info.Name, list.Export());
            };
            if (seat.Kind == LobbySeatKind.Open || (seat.Kind == LobbySeatKind.Person && !seat.Connected) || (seat.Kind == LobbySeatKind.Computer && !isEvent))
            {
                if (!isEvent) row.AddChild(computerDeck);
                row.AddChild(computer);
            }
            if (seat.Kind != LobbySeatKind.Open)
            {
                var free = BoardStyle.MakeButton("Free", 14);
                free.TooltipText = "Remove whoever sits here";
                free.Pressed += () => hosted.Open(index);
                row.AddChild(free);
            }
        }
        return row;
    }
}
