-- Arcade launcher: free play for every game.
--  * Games with a "Free Play" DIP switch / setting get it switched on.
--  * Games without one (settings kept in EEPROM: Konami, Sega System 32, Atari...) get "coin on start":
--    the player's Start button is taken over, and pressing it inserts a coin and then presses Start.
local exports = {
	name = 'freeplay',
	version = '1.0',
	description = 'Arcade launcher: free play for every game',
	license = 'BSD-3-Clause',
	author = { name = 'Arcade launcher' } }

local freeplay = exports
local frame_sub, stop_sub, reset_sub

-- frames: coin held 1..COIN_END, gap, then start held START_BEGIN..START_END
local COIN_END, START_BEGIN, START_END = 6, 18, 28

-- Per-game tables, from local.lua beside this file (copy local.example.lua): which games were set to free play in
-- their own operator menus, and which keep a credit topped up in RAM. Nothing here is game-specific.
NATIVE, CREDIT_KEEP = { }, { }
-- Input codes wired to on/off switches rather than buttons (e.g. 'JOYCODE_3_BUTTON5'): flipping one either way
-- counts as pressing Start, since it may already be on when the game boots.
TOGGLES = { }
do
	local ok, t = pcall(function()
		local p = manager.plugins['freeplay']
		return dofile(((p and p.directory) or 'plugins/freeplay') .. '/local.lua')
	end)
	if ok and type(t) == 'table' then
		NATIVE = t.native or NATIVE
		CREDIT_KEEP = t.credit_keep or CREDIT_KEEP
		TOGGLES = t.toggles or TOGGLES
	elseif not ok and not tostring(t):find('No such file') and not tostring(t):find('cannot open') then
		emu.print_error('freeplay: local.lua: ' .. tostring(t))
	end
end
function freeplay.startplugin()
	local players = { }   -- coin-on-start state per player
	local saved = { }     -- original Start input sequences (kept across soft resets)
	local keep, keepframe -- credit keeper for this game, frames since reset

	local function is_free(name)
		local n = name:lower()
		return n:find('^free play') ~= nil
	end

	local function start()
		local ioport = manager.machine.ioport
		local input = manager.machine.input
		local dip = ioport:token_to_input_type('DIPSWITCH')
		local conf = ioport:token_to_input_type('CONFIG')
		local found = false
		players = { }
		keep = nil

		local name = manager.machine.system.name
		if CREDIT_KEEP[name] then
			local k = CREDIT_KEEP[name]
			keep = { space = manager.machine.devices[k.cpu].spaces['program'], addr = k.addr, min = k.min, after = k.after * 60 }
			keepframe = 0
			emu.print_info('freeplay: keeping credits topped up')
			return
		end

		-- 1. DIP switches / config settings
		for _, port in pairs(ioport.ports) do
			for fname, field in pairs(port.fields) do
				-- (not "Lives": some boards call their infinite-lives setting "Free Play")
				if (field.type == dip or field.type == conf) and not fname:lower():find('lives') then
					local want
					if is_free(fname) then
						-- a switch called "Free Play": pick its On setting
						for value, sname in pairs(field.settings) do
							if sname:lower() == 'on' or sname:lower() == 'yes' then want = value end
						end
					else
						-- a setting (Coinage, Coin A, Difficulty...) with a "Free Play" choice
						for value, sname in pairs(field.settings) do
							if is_free(sname) then want = value end
						end
					end
					if want then
						field.user_value = want
						found = true
						emu.print_verbose('freeplay: ' .. fname .. ' set to free play')
					end
				end
			end
		end
		if found then
			emu.print_info('freeplay: DIP switch')
			return
		end
		-- games set to free play in their own operator menu (kept in their NVRAM/EEPROM): nothing to do
		if NATIVE[manager.machine.system.name] then
			emu.print_info('freeplay: set in the game\'s own settings')
			return
		end

		-- 2. coin on start
		local coins, starts = { }, { }
		-- slots the launcher's character select switched off (ARCADE_SLOTS_OFF="3,5,6"): their Start stays empty
		local off = { }
		for n in (os.getenv('ARCADE_SLOTS_OFF') or ''):gmatch('%d+') do off[tonumber(n)] = true end
		for p = 1, 8 do
			if not off[p] then
				coins[ioport:token_to_input_type('COIN' .. p)] = p
				starts[ioport:token_to_input_type('START' .. p)] = p
			end
		end
		for _, port in pairs(ioport.ports) do
			for _, field in pairs(port.fields) do
				local cp, sp = coins[field.type], starts[field.type]
				if cp then
					-- (some boards have the same coin input twice, e.g. main and sound board, and only one is
					-- read: press them all)
					players[cp] = players[cp] or { }
					players[cp].coins = players[cp].coins or { }
					table.insert(players[cp].coins, field)
					players[cp].coin = field
				elseif sp then
					players[sp] = players[sp] or { }
					players[sp].start = field
				end
			end
		end
		local empty = input:seq_from_tokens('')
		local any_start = false
		for _, pl in pairs(players) do if pl.start then any_start = true end end
		for p, pl in pairs(players) do
			if pl.coin and pl.start then
				local key = p
				-- keep the mapping as a token string: the seq object may be a reference into the field
				if not saved[key] then
					-- copy the mapping (the seq object is a reference into the field)
					local cur = pl.start:input_seq('standard')
					local ok, copy = pcall(emu.input_seq, cur)
					if not ok then copy = input:seq_from_tokens(input:seq_to_tokens(cur)) end
					-- never empty: MAME saves the emptied mapping in the game's .cfg on exit (the launcher strips it
					-- before each launch, but be safe) - fall back to the arcade.cfg mapping
					local tokens = input:seq_to_tokens(copy)
					if tokens == '' or tokens == 'NONE' then
						copy = ioport:type_seq(ioport:token_to_input_type('START' .. p))
						tokens = input:seq_to_tokens(copy)
					end
					-- (MAME's built-in mapping only as a last resort: JOYCODE_<p> isn't necessarily player p's controller)
					if tokens == '' or tokens == 'NONE' then
						copy = input:seq_from_tokens('KEYCODE_' .. p .. ' OR JOYCODE_' .. p .. '_START')
					end
					saved[key] = copy
				end
				pl.seq = saved[key]
				emu.print_verbose('freeplay: P' .. p .. ' start = ' .. input:seq_to_tokens(pl.seq))
				pl.start:set_input_seq('standard', empty)
				pl.frame = nil
				pl.down = false
				-- on/off switches in this player's Start mapping, with their state at boot
				pl.toggles = { }
				local tokens = ' ' .. input:seq_to_tokens(pl.seq) .. ' '
				for _, tok in ipairs(TOGGLES) do
					if tokens:find(' ' .. tok .. ' ', 1, true) then
						local code = input:code_from_token(tok)
						pl.toggles[#pl.toggles + 1] = { code = code, on = input:code_pressed(code) }
						emu.print_verbose('freeplay: P' .. p .. ' ' .. tok .. ' is an on/off switch, now ' .. (input:code_pressed(code) and 'on' or 'off'))
					end
				end
			elseif pl.coin and not any_start then
				-- no Start buttons on the board at all (a coin joins the game): the player's
				-- usual Start (MAME's default mapping for START<p>) just drops a coin in for that player
				pl.seq = ioport:type_seq(ioport:token_to_input_type('START' .. p))
				if input:seq_to_tokens(pl.seq) == '' then
					pl.seq = input:seq_from_tokens('KEYCODE_' .. p .. ' OR JOYCODE_' .. p .. '_START')
				end
				pl.frame = nil
				pl.down = false
				emu.print_verbose('freeplay: P' .. p .. ' has no start button, start = coin (' .. input:seq_to_tokens(pl.seq) .. ')')
			else
				players[p] = nil
			end
		end
		emu.print_info('freeplay: coin on start')
	end

	local function frame()
		local input = manager.machine.input
		if keep then
			keepframe = keepframe + 1
			if keepframe > keep.after and keep.space:read_u8(keep.addr) < keep.min then keep.space:write_u8(keep.addr, keep.min) end
			return
		end
		for p, pl in pairs(players) do
			local down = input:seq_pressed(pl.seq)
			local flipped = false
			for _, tg in ipairs(pl.toggles or { }) do
				local on = input:code_pressed(tg.code)
				if on ~= tg.on then flipped = true; tg.on = on end
			end
			if ((down and not pl.down) or flipped) and not pl.frame then
				pl.frame = 0
				emu.print_verbose('freeplay: start pressed, inserting coin')
			end
			pl.down = down
			if pl.frame then
				pl.frame = pl.frame + 1
				local f = pl.frame
				for _, c in ipairs(pl.coins) do
					if f <= COIN_END then c:set_value(1) else c:clear_value() end
				end
				if pl.start then
					if f >= START_BEGIN and f <= START_END then pl.start:set_value(1) else pl.start:clear_value() end
				end
				if f > START_END then pl.frame = nil end
			end
		end
	end

	local function stop()
		-- (too late to restore the Start mapping: MAME has already written the .cfg by now)
		saved = { }
		players = { }
	end

	frame_sub = emu.add_machine_frame_notifier(frame)
	stop_sub = emu.add_machine_stop_notifier(stop)
	reset_sub = emu.add_machine_reset_notifier(start)
end

return exports
