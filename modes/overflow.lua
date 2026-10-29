-- Overflow: bigger turns. Shows game settings and a step hook.
mode {
  id = "overflow",
  name = "Overflow",
  description = "Any casual deck. Each player may play two lands each turn, draws an additional card in their draw step, and has a maximum hand size of eight.",
  format = "casual",
  game = { landsPerTurn = 2, maxHandSize = 8 },
  emblem = { text = "You may play an additional land each turn.\nAt the beginning of your draw step, draw an additional card.\nYour maximum hand size is eight." },
}

function on_step(game, step)
  if step == "draw" then game.draw(game.active(), 1) end
end
