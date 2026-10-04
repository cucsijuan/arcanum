// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Net.Protocol;

/// <summary>
/// Everything sent between a host and its players. The host runs the game; players only ever receive what their
/// seat may see (their own <see cref="GameView"/>, events filtered for them) and send back answers to questions.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$m")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(Heartbeat), "beat")]
[JsonDerivedType(typeof(ViewUpdate), "view")]
[JsonDerivedType(typeof(Happened), "event")]
[JsonDerivedType(typeof(Ask), "ask")]
[JsonDerivedType(typeof(Answer), "answer")]
[JsonDerivedType(typeof(AskExpired), "expired")]
[JsonDerivedType(typeof(Stops), "stops")]
[JsonDerivedType(typeof(SeatStatus), "seat")]
[JsonDerivedType(typeof(Joined), "joined")]
[JsonDerivedType(typeof(LobbyState), "lobby")]
[JsonDerivedType(typeof(SubmitDeck), "deck")]
[JsonDerivedType(typeof(GameStarting), "starting")]
[JsonDerivedType(typeof(EventUpdate), "event-state")]
[JsonDerivedType(typeof(DraftPick), "pick")]
[JsonDerivedType(typeof(EventReady), "ready")]
public abstract record NetMessage;

// ------------------------------------------------------------------ connection

/// <summary>
/// First message of a player. <paramref name="Token"/> identifies their seat (also to reconnect); empty to take a free
/// seat in a lobby.
/// </summary>
/// <param name="Content">Identifies the card content (module and card data); both sides must use the same.</param>
public sealed record Hello(int Protocol, string Version, string Content, string Name, string Token) : NetMessage;

/// <summary>The player sits at <paramref name="Seat"/>; <paramref name="Players"/> are the names of every seat.</summary>
public sealed record Welcome(PlayerId Seat, IReadOnlyList<string> Players, bool Commander) : NetMessage;

public sealed record Rejected(string Reason) : NetMessage;

/// <summary>Sent regularly both ways so a silent connection is noticed as lost.</summary>
public sealed record Heartbeat : NetMessage;

// ------------------------------------------------------------------ lobby (before the game)

/// <summary>A player took <paramref name="Seat"/>; <paramref name="Token"/> lets them back in if they drop.</summary>
public sealed record Joined(int Seat, string Token) : NetMessage;

public enum LobbySeatKind { Open, Person, Computer }

/// <param name="Problem">Why the seat's deck can't be played (missing, illegal in the format...), or null.</param>
public sealed record LobbySeat(string Name, LobbySeatKind Kind, bool Connected, string? Deck, string? Problem);

/// <summary>The lobby as everyone sees it.</summary>
/// <param name="Event">For a limited event (draft, sealed): what it is; players bring no deck.</param>
public sealed record LobbyState(string Format, bool Commander, IReadOnlyList<LobbySeat> Seats, string? Event = null) : NetMessage;

/// <summary>The deck a player brings, as a deck list (one "count name" per line).</summary>
public sealed record SubmitDeck(string Name, string List) : NetMessage;

/// <summary>
/// The game begins: the player now says <see cref="Hello"/> to the game with their token. For a limited event
/// (<paramref name="Event"/>), the connection carries the event from now on, without a new greeting.
/// </summary>
public sealed record GameStarting(bool Event = false) : NetMessage;

// ------------------------------------------------------------------ game, host to player

/// <summary>The game as the player sees it now.</summary>
public sealed record ViewUpdate(GameView View) : NetMessage;

/// <summary>Something happened (in order; the view that follows shows the result).</summary>
public sealed record Happened(EventView Event) : NetMessage;

/// <summary>A decision the player must make. <paramref name="Error"/> explains why a previous answer was refused.</summary>
/// <param name="SecondsLeft">Time the player has to answer (the host's decision time limit); -1 when unlimited.</param>
public sealed record Ask(int Id, Question Question, string? Error = null, int SecondsLeft = -1) : NetMessage;

/// <summary>The player took too long to answer question <paramref name="Ask"/>: the computer answered it for them.</summary>
public sealed record AskExpired(int Ask) : NetMessage;

public enum SeatState { Connected, Disconnected, Computer }

/// <summary>
/// Who plays a seat now. While <see cref="SeatState.Disconnected"/>, <paramref name="SecondsLeft"/> counts down to the
/// computer taking over (-1: waiting for the host to decide).
/// </summary>
public sealed record SeatStatus(PlayerId Seat, SeatState State, int SecondsLeft = 0) : NetMessage;

// ------------------------------------------------------------------ game, player to host

/// <summary>The answer to question <paramref name="Ask"/>, in the type that question expects.</summary>
public sealed record Answer(int Ask, JsonElement Value) : NetMessage;

/// <summary>
/// The player's priority stops, so the host passes priority for them without asking over the network each time.
/// </summary>
public sealed record Stops(IReadOnlyList<Step> OwnTurn, IReadOnlyList<Step> OpponentTurn, bool FullControl, int? PassingTurn) : NetMessage;

// ------------------------------------------------------------------ questions (one per player decision)

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$q")]
[JsonDerivedType(typeof(KeepHandQuestion), "keep")]
[JsonDerivedType(typeof(BottomQuestion), "bottom")]
[JsonDerivedType(typeof(ActionQuestion), "action")]
[JsonDerivedType(typeof(ManaQuestion), "mana")]
[JsonDerivedType(typeof(TargetsQuestion), "targets")]
[JsonDerivedType(typeof(AttackQuestion), "attack")]
[JsonDerivedType(typeof(BlockQuestion), "block")]
[JsonDerivedType(typeof(DamageQuestion), "damage")]
[JsonDerivedType(typeof(YesNoQuestion), "yesno")]
[JsonDerivedType(typeof(DiscardQuestion), "discard")]
[JsonDerivedType(typeof(CardsQuestion), "cards")]
[JsonDerivedType(typeof(ModesQuestion), "modes")]
[JsonDerivedType(typeof(NumberQuestion), "number")]
[JsonDerivedType(typeof(OptionQuestion), "option")]
public abstract record Question;

public sealed record KeepHandQuestion(int MulligansTaken) : Question;
public sealed record BottomQuestion(int Count) : Question;
public sealed record ActionQuestion(IReadOnlyList<PlayerAction> Legal) : Question;
public sealed record ManaQuestion(ManaPaymentRequest Request) : Question;
public sealed record TargetsQuestion(TargetRequest Request) : Question;
public sealed record AttackQuestion(IReadOnlyList<CardId> PossibleAttackers, IReadOnlyList<PlayerId> Defenders) : Question;
public sealed record BlockQuestion(BlockRequest Request) : Question;
public sealed record DamageQuestion(DamageAssignmentRequest Request) : Question;
public sealed record YesNoQuestion(YesNoRequest Request) : Question;
public sealed record DiscardQuestion(int Count) : Question;
public sealed record CardsQuestion(CardChoiceRequest Request) : Question;
public sealed record ModesQuestion(ModeRequest Request) : Question;
public sealed record NumberQuestion(NumberRequest Request) : Question;
public sealed record OptionQuestion(OptionRequest Request) : Question;
