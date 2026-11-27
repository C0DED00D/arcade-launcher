-- Arcade launcher: character select for 4-player games where each player slot is a fixed character (P1 is always the
-- same hero, and so on). A two-player cabinet could otherwise only ever play the first two characters.
--  * At boot (after the save-state picker, if that's up) a screen shows the characters. The LEFT and RIGHT players
--    (the controls normally on P1 and P2) each move to a character; button 1 or Start = ready, button 2 = undo.
--    The game starts when LEFT is ready; RIGHT gets the character it's on then (never the same one as LEFT).
--  * Each side's joystick, buttons, coin and Start are then moved onto its character's player slot, and taken off
--    the slots nobody picked. Start goes through the freeplay plugin when that has taken Start over (coin on start).
--  * MAME writes changed mappings into cfg\<set>.cfg on exit, so the originals are kept in undo_<set>.txt beside this
--    file and put back at the next boot. A soft reset (F3) shows the screen again.
-- Which games, and their characters per player slot: local.lua beside this file, e.g.
--   return { games = { mygame = { { name = 'HERO', color = 0xffe03030 }, ... one per player slot } } }
-- (parent names cover clones).
local exports = {
	name = 'charselect',
	version = '1.0',
	description = 'Arcade launcher: character select for fixed-character 4-player games',
	license = 'BSD-3-Clause',
	author = { name = 'Arcade launcher' } }

local charselect = exports
local reset_sub, stop_sub

local DIR
do
	local p = manager.plugins['charselect']
	DIR = (p and p.directory) or 'plugins/charselect'
end

local GAMES = { }
do
	local ok, t = pcall(function() return dofile(DIR .. '/local.lua') end)
	if ok and type(t) == 'table' then
		GAMES = t.games or GAMES
	elseif not ok and not tostring(t):find('No such file') and not tostring(t):find('cannot open') then
		emu.print_error('charselect: local.lua: ' .. tostring(t))
	end
end

-- the same marker the save-state picker uses (env ARCADE_MENU_MARKER, set by the launcher for the MAME it starts):
-- the launcher turns the CRT shader off while it exists
local MARKER = os.getenv('ARCADE_MENU_MARKER')

local KINDS = { 'JOYSTICK_UP', 'JOYSTICK_DOWN', 'JOYSTICK_LEFT', 'JOYSTICK_RIGHT',
	'BUTTON1', 'BUTTON2', 'BUTTON3', 'BUTTON4' }
local BOOT_AT = 0.5   -- emulated seconds before the screen can open (the save-state picker opens at 0.2)

function charselect.startplugin()
	local sys, chars            -- set name, its character list
	local fields                -- [slot][kind] = field (kind: KINDS entries, 'START', 'COIN')
	local orig                  -- [slot][kind] = token string as at boot
	local menu                  -- { cur = {1, 2}, ready = {false, false} } while the screen is up
	local pending               -- the screen still has to open this boot
	local was = { }             -- previous input state for edge detection

	local function input() return manager.machine.input end
	local function seq(tokens) return input():seq_from_tokens(tokens or '') end
	local function down(tokens) return tokens and tokens ~= '' and input():seq_pressed(seq(tokens)) end
	local fake = { }            -- test scripts: [side .. 'L' / 'R' / 'OK' / 'BACK'] = true for one poll
	local function edge(key, d)
		d = d or fake[key]; fake[key] = nil
		local e = d and not was[key]; was[key] = d; return e
	end

	local function undo_file() return DIR .. '/undo_' .. sys .. '.txt' end

	-- every player field of the game, by slot and kind
	local function find_fields()
		local io = manager.machine.ioport
		fields = { { }, { }, { }, { } }
		for _, port in pairs(io.ports) do
			for _, f in pairs(port.fields) do
				local t = io:input_type_to_token(f.type, f.player)
				local p, kind = t:match('^P(%d)_(.+)$')
				if not p then
					kind, p = t:match('^(START)(%d)$')
					if not kind then kind, p = t:match('^(COIN)(%d)$') end
				end
				p = tonumber(p)
				if p and p >= 1 and p <= 4 and kind and not fields[p][kind] then fields[p][kind] = f end
			end
		end
	end

	-- put back what the last run changed (MAME saved the changed mappings into the game's cfg). Back to MAME's
	-- "DEFAULT" placeholder (the driver's mapping with the launcher's ctrlr file on top): an explicit copy of the same
	-- keys would be written into the cfg again, the placeholder isn't.
	local function restore()
		local f = io.open(undo_file(), 'r')
		if not f then return end
		for line in f:lines() do
			local slot, kind = line:match('^(%d)\t([%w_]+)')
			slot = tonumber(slot)
			local fld = slot and fields[slot] and fields[slot][kind]
			if fld then fld:set_input_seq('standard', seq('DEFAULT')) end
		end
		f:close()
		os.remove(undo_file())
	end

	local function capture()
		local io = manager.machine.ioport
		orig = { { }, { }, { }, { } }
		for slot = 1, 4 do
			for kind, fld in pairs(fields[slot]) do orig[slot][kind] = input():seq_to_tokens(fld:input_seq('standard')) end
			-- Start / coin as the player presses them: the type's own mapping (freeplay may have emptied START<n>,
			-- and some games have no Start input at all)
			for _, kind in ipairs({ 'START', 'COIN' }) do
				local ok, t = pcall(function() return io:token_to_input_type(kind .. slot) end)
				if ok and t then orig[slot][kind .. '_TYPE'] = input():seq_to_tokens(io:type_seq(t)) end
			end
		end
	end

	local function marker(on)
		if not MARKER or MARKER == '' then return end
		if on then local f = io.open(MARKER, 'w'); if f then f:write('1'); f:close() end else os.remove(MARKER) end
	end

	local function open()
		menu = { cur = { 1, 2 }, ready = { false, false }, was_paused = manager.machine.paused }
		if not menu.was_paused then emu.pause() end
		marker(true)
		-- ignore buttons already held (the player who just picked a save state, say)
		for side = 1, 2 do
			for _, k in ipairs({ 'L', 'R', 'OK', 'BACK' }) do was[side .. k] = true end
		end
	end

	-- move each side's controls onto its character's slot
	local function apply()
		local pick = { [menu.cur[1]] = 1 }
		if menu.cur[2] ~= menu.cur[1] then pick[menu.cur[2]] = 2 end
		-- freeplay emptied START<n> (coin on start): leave Start's mapping alone and hand it the presses instead
		local start_taken = false
		for slot = 1, 2 do
			local fld = fields[slot].START
			if fld and input():seq_to_tokens(fld:input_seq('standard')) == '' then start_taken = true end
		end
		-- the originals, for the next boot
		local f = io.open(undo_file(), 'w')
		if f then
			for slot = 1, 4 do
				for kind in pairs(fields[slot]) do
					if not (kind == 'START' and start_taken) then f:write(slot, '\t', kind, '\t', orig[slot][kind], '\n') end
				end
			end
			f:close()
		end
		charselect_start = { }
		for slot = 1, 4 do
			local side = pick[slot]
			for kind, fld in pairs(fields[slot]) do
				local tokens
				if kind == 'START' and start_taken then
					tokens = nil                                   -- left to freeplay
				elseif side then
					tokens = (kind == 'START' or kind == 'COIN') and orig[side][kind .. '_TYPE'] or orig[side][kind] or ''
				elseif slot <= 2 then
					tokens = ''                                    -- a side's own slot nobody picked: switched off
				end
				if tokens then fld:set_input_seq('standard', seq(tokens)) end
			end
			-- for freeplay: which presses start this slot ('' = none)
			if side then charselect_start[slot] = seq(orig[side].START_TYPE)
			elseif slot <= 2 then charselect_start[slot] = seq('') end
		end
		local names = { }
		for slot, side in pairs(pick) do names[#names + 1] = (side == 1 and 'LEFT' or 'RIGHT') .. ' = ' .. chars[slot].name end
		table.sort(names)
		manager.machine:popmessage(table.concat(names, '    '))
		emu.print_info('charselect: ' .. table.concat(names, ', '))
	end

	local function close()
		if not menu.was_paused then emu.unpause() end
		marker(false)
		menu = nil
	end

	local function step(side, d)
		local n = #chars
		local other = menu.cur[3 - side]
		local c = menu.cur[side]
		for _ = 1, n do
			c = (c - 1 + d) % n + 1
			if not (menu.ready[3 - side] and c == other) then break end
		end
		menu.cur[side] = c
	end

	local function menu_input()
		for side = 1, 2 do
			local o = orig[side]
			local l = edge(side .. 'L', down(o.JOYSTICK_LEFT))
			local r = edge(side .. 'R', down(o.JOYSTICK_RIGHT))
			local ok = edge(side .. 'OK', down(o.BUTTON1) or down(o.START_TYPE))
			local back = edge(side .. 'BACK', down(o.BUTTON2))
			if not menu.ready[side] then
				if l then step(side, -1) end
				if r then step(side, 1) end
				if ok and not (menu.ready[3 - side] and menu.cur[3 - side] == menu.cur[side]) then menu.ready[side] = true end
			elseif back then
				menu.ready[side] = false
			end
		end
		if menu.ready[1] then
			-- RIGHT can't share LEFT's character
			if menu.cur[2] == menu.cur[1] then step(2, 1) end
			apply()
			close()
		end
	end

	local function draw()
		local c = manager.machine.render.ui_container
		c:draw_box(0, 0, 1, 1, 0xe8000000, 0xe8000000)
		c:draw_text('center', 0.05, 'CHOOSE YOUR CHARACTER', 0xffffd040)
		local n = #chars
		local gap = 0.025
		local w = (1 - gap * (n + 1)) / n
		local top, bottom = 0.22, 0.70
		for i, ch in ipairs(chars) do
			local x = gap + (i - 1) * (w + gap)
			local here = { }
			for side = 1, 2 do if menu.cur[side] == i then here[#here + 1] = side end end
			local lit = #here > 0
			local col = ch.color or 0xffc0c0c0
			local dim = (col & 0x00ffffff) | 0x50000000
			c:draw_box(x, top, x + w, bottom, col, lit and col or dim)
			c:draw_text(x + 0.01, top + 0.02, 'PLAYER ' .. i, 0xffffffff)
			local ok, tw = pcall(function() return manager.ui:get_string_width(ch.name) end)
			tw = (ok and tw) or 0
			c:draw_text(x + math.max(0.005, (w - tw) / 2), (top + bottom) / 2 - 0.02, ch.name, 0xffffffff)
			for k, side in ipairs(here) do
				local label = (side == 1 and 'LEFT' or 'RIGHT') .. (menu.ready[side] and '  READY' or '')
				local y = bottom + 0.03 + (k - 1) * 0.06
				c:draw_box(x, y, x + w, y + 0.05, 0xffffffff, side == 1 and 0xff2050c0 or 0xffc03020)
				local ok2, lw = pcall(function() return manager.ui:get_string_width(label) end)
				lw = (ok2 and lw) or 0
				c:draw_text(x + math.max(0.005, (w - lw) / 2), y + 0.008, label, 0xffffffff)
			end
		end
		c:draw_text('center', 0.90, 'LEFT and RIGHT player: move to pick   BUTTON 1 / START: ready   BUTTON 2: change', 0xffc0c0c0)
		c:draw_text('center', 0.94, 'The game starts when LEFT is ready', 0xff909090)
	end

	local function start()
		charselect_start = nil
		menu, pending, chars = nil, false, nil
		local s = manager.machine.system
		chars = GAMES[s.name] or (s.parent and GAMES[s.parent])
		if not chars then return end
		sys = s.name
		find_fields()
		restore()
		capture()
		pending = true
	end

	local function poll()
		if menu then menu_input(); return end
		if not pending then return end
		if manager.machine.time:as_double() < BOOT_AT then return end
		if savestates_api and savestates_api.state() then return end   -- wait for the save-state picker
		pending = false
		open()
	end

	-- for test scripts: charselect_api.state(), .press(side, what) with what = 'L' / 'R' / 'OK' / 'BACK'
	charselect_api = {
		state = function() if not menu then return nil end return menu.cur[1], menu.cur[2], menu.ready[1], menu.ready[2] end,
		pending = function() return pending end,
		press = function(side, what) fake[side .. what] = true end,
	}

	reset_sub = emu.add_machine_reset_notifier(start)
	stop_sub = emu.add_machine_stop_notifier(function() if menu then close() end; sys = nil; pending = false end)
	emu.register_periodic(poll)
	emu.register_frame_done(function() if menu then draw() end end)
end

return exports
