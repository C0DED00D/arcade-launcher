-- Copy to local.lua and list your own games. local.lua is not part of the repository.
--
-- native: games you've set to FREE PLAY in their own operator / test menu (saved in nvram\<set>), so the plugin
--         leaves them alone instead of doing "coin on start".  config\svcdrive.lua can script those menus.
-- credit_keep: games with no free play at all and no Start button: keep a RAM credit counter at `min` once `after`
--         seconds have passed. Find the address with config\creditfind.lua.
local NATIVE = {
	-- examplegame = true,
	-- ['1234game'] = true,
}

local CREDIT_KEEP = {
	-- examplegame = { cpu = ':maincpu', addr = 0x00E0, min = 1, after = 12 },
}

return { native = NATIVE, credit_keep = CREDIT_KEEP }
