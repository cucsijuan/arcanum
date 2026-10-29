-- Life Race: the first player to reach 40 life wins. A small example of a game mode (see docs/game-modes.md).
mode {
  id = "life-race",
  name = "Life Race",
  description = "Any casual deck. Everyone gains 1 life at the beginning of their upkeep, and the first player to reach 40 life wins the game.",
  format = "casual",
  emblem = {
    abilities = {
      { trigger = "upkeep", effects = { { gainLife = 1 } }, text = "At the beginning of your upkeep, you gain 1 life." },
    },
    text = "At the beginning of your upkeep, you gain 1 life.\nWhen you have 40 or more life, you win the game.",
  },
}

function check(game)
  for _, p in ipairs(game.living()) do
    if game.player(p).life >= 40 then
      game.announce(game.player(p).name .. " reached 40 life!")
      game.win(p)
      return
    end
  end
end
