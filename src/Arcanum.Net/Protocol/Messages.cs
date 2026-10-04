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
[JsonDerivedType(typeof(Stops), "stops")]
[JsonDerivedType(typeof(SeatStatus), "seat")]
public abstract record NetMessage;

// ------------------------------------------------------------------ connection

/// <summary>First message of a player. <paramref name="Token"/> identifies their seat (also to reconnect).</summary>
/// <param name="Content">Identifies the card content (module and card data); both sides must use the same.</param>
public sealed record Hello(int Protocol, string Version, string Content, string Name, string Token) : NetMessage;

/// <summary>The player sits at <paramref name="Seat"/>; <paramref name="Players"/> are the names of every seat.</summary>
public sealed record Welcome(PlayerId Seat, IReadOnlyList<string> Players, bool Commander) : NetMessage;

public sealed record Rejected(string Reason) : NetMessage;

/// <summary>Sent regularly both ways so a silent connection is noticed as lost.</summary>
public sealed record Heartbeat : NetMessage;

// ------------------------------------------------------------------ game, host to player

/// <summary>The game as the player sees it now.</summary>
public sealed record ViewUpdate(GameView View) : NetMessage;

/// <summary>Something happened (in order; the view that follows shows the result).</summary>
public sealed record Happened(EventView Event) : NetMessage;

/// <summary>A decision the player must make. <paramref name="Error"/> explains why a previous answer was refused.</summary>
public sealed record Ask(int Id, Question Question, string? Error = null) : NetMessage;

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
