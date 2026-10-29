-- Sudden Death: a game with a turn limit. Shows events, announcements and a mode's own way to end the game.
mode {
  id = "sudden-death",
  name = "Sudden Death",
  description = "Any casual deck, two players. After the tenth turn the player with the most life wins; with equal life totals the next player to lose life loses the game.",
  format = "casual",
  players = { min = 2, max = 2 },
  emblem = { text = "After the tenth turn, the player with the most life wins the game.\nIf life totals are equal then, the next player to lose life loses the game." },
}

local LastTurn = 10
local tied = false

function on_step(game, step)
  if step == "upkeep" and game.turn() == LastTurn then game.announce("Last turn!") end
  if step == "cleanup" and game.turn() >= LastTurn and not tied then
    local a, b = game.player(1), game.player(2)
    if a.life > b.life then game.win(1)
    elseif b.life > a.life then game.win(2)
    else
      tied = true
      game.announce("Life totals are equal: the next player to lose life loses the game.")
    end
  end
end

function on_event(game, e)
  if tied and e.type == "LifeChanged" and e.newLife < e.oldLife then
    game.lose(e.player, "lost life in sudden death")
  end
end
