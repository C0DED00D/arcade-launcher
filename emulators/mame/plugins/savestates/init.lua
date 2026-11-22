-- Arcade launcher: save states.
--  * SAVE (F6): saves the game into the next of 10 rotating slots, with a screenshot and the time.
--  * LOAD (F7): a tap loads the newest save; holding it opens a picker of all saves (screenshot + time).
--    In the picker: joystick / arrows move, button 1 (or Enter) loads, button 2 or LOAD again closes.
--  * Starting a game that has saves opens the picker paused, with a "start from the beginning" tile first;
--    START or button 2 there also just starts the game normally.
-- The launcher turns pad buttons into these keys (see GameRunner); arcade encoders can send them directly.
-- Files: sta\<set>\slotN.sta (MAME state), snap\savestates\<set>\slotN.png and slots.txt (slot<TAB>unix time).
local exports = {
	name = 'savestates',
	version = '1.0',
	description = 'Arcade launcher: 10 rotating save states with a screenshot picker',
	license = 'BSD-3-Clause',
	author = { name = 'Arcade launcher' } }

local savestates = exports
local reset_sub, stop_sub   -- notifier subscriptions must stay referenced or they stop firing

local SLOTS = 10
local SAVE_KEYS = 'KEYCODE_F6'
local LOAD_KEYS = 'KEYCODE_F7'
local START_KEYS = 'KEYCODE_1 OR JOYCODE_1_START'
local HOLD = 0.45          -- seconds LOAD must be held to open the picker
local BOOT_DELAY = 0.2     -- emulated seconds before the start-up picker opens
local COLS = 5

function savestates.startplugin()
	local sys, snapdir                -- current set, its screenshot folder
	local slots = { }                 -- [n] = unix time of the save
	local last = 0                    -- slot written last
	local save_seq, load_seq, start_seq
	local save_was, load_was = false, false
	local booted, boot_pending = false, false
	local load_since                  -- emulated time LOAD went down (nil = up)
	local menu                        -- picker state while open
	local nav_was = { }

	local function input() return manager.machine.input end

	local function seq_down(seq) return seq and input():seq_pressed(seq) end

	local function type_down(token)
		local io = manager.machine.ioport
		local ok, t = pcall(function() return io:token_to_input_type(token) end)
		if not ok or not t then return false end
		return input():seq_pressed(io:type_seq(t))
	end

	local function first_dir(opt)
		local v = manager.options.entries[opt]:value()
		return (v:match('^[^;]+') or v)
	end

	local function meta_path() return snapdir .. '/slots.txt' end

	local function read_meta()
		slots, last = { }, 0
		local f = io.open(meta_path(), 'r')
		if not f then return end
		for line in f:lines() do
			local n, t = line:match('^(%d+)%s+(%d+)')
			if n then slots[tonumber(n)] = tonumber(t) end
			local l = line:match('^last%s+(%d+)')
			if l then last = tonumber(l) end
		end
		f:close()
	end

	local function write_meta()
		local f = io.open(meta_path(), 'w')
		if not f then emu.print_error('savestates: cannot write ' .. meta_path()); return end
		f:write('last\t' .. last .. '\n')
		for n = 1, SLOTS do if slots[n] then f:write(n .. '\t' .. slots[n] .. '\n') end end
		f:close()
	end

	local function newest()
		local best, bt
		for n, t in pairs(slots) do if not bt or t > bt then best, bt = n, t end end
		return best
	end

	local function ago(t)
		local d = os.time() - t
		if d < 60 then return 'just now' end
		if d < 3600 then return string.format('%d min ago', d // 60) end
		if d < 86400 then return string.format('%d hr ago', d // 3600) end
		return string.format('%d days ago', d // 86400)
	end

	local function stamp(t) return os.date('%b %d  %I:%M:%S %p', t):gsub(' 0(%d:)', ' %1') end

	local function do_save()
		if manager.machine.system.supports_save == false then
			manager.machine:popmessage('This game does not support save states')
			return
		end
		local n = last % SLOTS + 1
		local scr = manager.machine.screens:at(1)
		-- relative to the snapshot folder; MAME creates the folders
		if scr then scr:snapshot('savestates/' .. sys .. '/slot' .. n .. '.png') end
		manager.machine:save('slot' .. n)          -- MAME writes sta\<set>\slotN.sta
		slots[n] = os.time()
		last = n
		write_meta()
		manager.machine:popmessage(string.format('SAVED  (slot %d of %d)', n, SLOTS))
	end

	local function do_load(n)
		if not n or not slots[n] then manager.machine:popmessage('No save states yet'); return end
		manager.machine:load('slot' .. n)
		manager.machine:popmessage(string.format('LOADED  slot %d  -  %s', n, ago(slots[n])))
	end

	-- picker ----------------------------------------------------------------------------------------
	local function close_menu()
		if not menu then return end
		for _, e in ipairs(menu.entries) do if e.tex then e.tex:free() end end
		if not menu.was_paused then emu.unpause() end
		menu = nil
	end

	-- boot = true: the picker shown when a game with saves starts. It gets a "start from the beginning" tile first
	-- (newest save selected), and cancelling it just lets the game boot normally.
	local function open_menu(boot)
		read_meta()
		local entries = { }
		for n, t in pairs(slots) do entries[#entries + 1] = { slot = n, time = t } end
		if #entries == 0 then
			if not boot then manager.machine:popmessage('No save states yet') end
			return
		end
		table.sort(entries, function(a, b) return a.time > b.time end)
		for _, e in ipairs(entries) do
			local f = io.open(snapdir .. '/slot' .. e.slot .. '.png', 'rb')
			if f then
				local data = f:read('a'); f:close()
				local ok, bmp = pcall(emu.bitmap_argb32.load, data)
				if ok and bmp then
					e.bmp = bmp
					e.tex = manager.machine.render:texture_alloc(bmp)
					e.aspect = (bmp.width >= bmp.height) and (4 / 3) or (3 / 4)
				end
			end
		end
		if boot then table.insert(entries, 1, { fresh = true }) end
		menu = { entries = entries, sel = boot and 2 or 1, boot = boot, was_paused = manager.machine.paused }
		if not menu.was_paused then emu.pause() end
		nav_was = { left = true, right = true, up = true, down = true, ok = true, back = true, start = true }
	end

	local function pick()
		local e = menu.entries[menu.sel]
		close_menu()
		if e.fresh then manager.machine:popmessage('STARTING FROM THE BEGINNING') else do_load(e.slot) end
	end

	local function nav(name, down)
		local edge = down and not nav_was[name]
		nav_was[name] = down
		return edge
	end

	local function menu_input()
		local m = menu
		local count = #m.entries
		if nav('left', type_down('P1_JOYSTICK_LEFT') or type_down('UI_LEFT')) then m.sel = (m.sel - 2) % count + 1 end
		if nav('right', type_down('P1_JOYSTICK_RIGHT') or type_down('UI_RIGHT')) then m.sel = m.sel % count + 1 end
		if nav('up', type_down('P1_JOYSTICK_UP') or type_down('UI_UP')) and m.sel > COLS then m.sel = m.sel - COLS end
		if nav('down', type_down('P1_JOYSTICK_DOWN') or type_down('UI_DOWN')) and m.sel + COLS <= count then m.sel = m.sel + COLS end
		-- At boot Enter (UI_SELECT) doesn't pick: the launcher taps Enter for a while to dismiss MAME's warning screen.
		if nav('ok', type_down('P1_BUTTON1') or (not m.boot and type_down('UI_SELECT'))) then pick(); return end
		if nav('back', type_down('P1_BUTTON2')) then close_menu(); return end
		if m.boot and nav('start', seq_down(start_seq)) then
			close_menu(); manager.machine:popmessage('STARTING FROM THE BEGINNING')
		end
	end

	-- centred text inside a box (container units)
	local function text_in(c, x0, x1, y, s, color)
		local ok, w = pcall(function() return manager.ui:get_string_width(s) end)
		if not ok or not w then w = 0 end
		c:draw_text(math.max(x0, (x0 + x1 - w) / 2), y, s, color)
	end

	local function draw_menu()
		local c = manager.machine.render.ui_container
		local tgt = manager.machine.render.ui_target
		local A = tgt.width / tgt.height              -- screen aspect (container units are 0..1 both ways)
		c:draw_box(0, 0, 1, 1, 0xe0000000, 0xe0000000)
		c:draw_text('center', 0.035, menu.boot and 'CONTINUE FROM A SAVE?' or 'LOAD STATE', 0xffffd040)
		local rows = math.ceil(#menu.entries / COLS)
		local aspect = 4 / 3
		for _, e in ipairs(menu.entries) do if e.aspect then aspect = e.aspect; break end end
		local gap = 0.02
		local cw = (1 - gap * (COLS + 1)) / COLS
		local th = cw * A / aspect
		local maxh = math.min(0.30, 0.80 / rows - 0.085)   -- 11 tiles (boot picker) need a third row
		if th > maxh then th = maxh; cw = th * aspect / A end
		local rowh = th + 0.085
		local total_w = COLS * cw + (COLS - 1) * gap
		local x0 = (1 - total_w) / 2
		local y0 = math.max(0.1, (1 - rows * rowh) / 2 + 0.02)
		for i, e in ipairs(menu.entries) do
			local col = (i - 1) % COLS
			local row = (i - 1) // COLS
			local x = x0 + col * (cw + gap)
			local y = y0 + row * rowh
			local sel = (i == menu.sel)
			if sel then c:draw_box(x - 0.006, y - 0.010, x + cw + 0.006, y + th + 0.010, 0xffffd040, 0xffffd040) end
			local col_txt = sel and 0xffffffff or 0xffb0b0b0
			if e.fresh then
				c:draw_box(x, y, x + cw, y + th, 0xff606060, sel and 0xff283860 or 0xff181c28)
				local ok, w = pcall(function() return manager.ui:get_string_width('THE BEGINNING') end)
				if ok and w and w > cw * 0.95 then   -- narrow tile (vertical game in a window)
					text_in(c, x, x + cw, y + th / 2 - 0.035, 'START', col_txt)
					text_in(c, x, x + cw, y + th / 2 + 0.005, 'OVER', col_txt)
				else
					text_in(c, x, x + cw, y + th / 2 - 0.035, 'START FROM', col_txt)
					text_in(c, x, x + cw, y + th / 2 + 0.005, 'THE BEGINNING', col_txt)
				end
				c:draw_text(x + 0.004, y + th + 0.014, 'New game', col_txt)
			else
				if e.tex then c:draw_quad(e.tex, x, y, x + cw, y + th, sel and 0xffffffff or 0xffa0a0a0)
				else c:draw_box(x, y, x + cw, y + th, 0xff606060, 0xff202020) end
				c:draw_text(x + 0.004, y + th + 0.014, stamp(e.time), col_txt)
				c:draw_text(x + 0.004, y + th + 0.044, ago(e.time), sel and 0xffffd040 or 0xff808080)
			end
		end
		if menu.boot then
			c:draw_text('center', 0.95, 'MOVE: select     BUTTON 1: play     START / BUTTON 2: new game', 0xffc0c0c0)
		else
			c:draw_text('center', 0.95, 'MOVE: select     BUTTON 1: load     BUTTON 2 / LOAD: cancel', 0xffc0c0c0)
		end
	end

	-- wiring ----------------------------------------------------------------------------------------
	local function start()
		sys = manager.machine.system.name
		snapdir = first_dir('snapshot_directory') .. '/savestates/' .. sys
		save_seq = input():seq_from_tokens(SAVE_KEYS)
		load_seq = input():seq_from_tokens(LOAD_KEYS)
		-- raw codes, not START1's mapping: the freeplay plugin empties that in coin-on-start games
		start_seq = input():seq_from_tokens(START_KEYS)
		read_meta()
		save_was, load_was, load_since = true, true, nil   -- ignore keys already held at start
		close_menu()
		-- only when the game is first started, not on a soft reset (F3)
		boot_pending = not booted
		booted = true
	end

	local function poll()
		if not sys then return end
		-- Game just started and has saves: offer them. Waits for a little emulated time, so MAME's own startup
		-- warning screen (which holds the machine) is gone first.
		if boot_pending and manager.machine.time:as_double() >= BOOT_DELAY then
			boot_pending = false
			if manager.machine.system.supports_save ~= false and next(slots) then open_menu(true) end
		end
		local s = seq_down(save_seq)
		local l = seq_down(load_seq)
		if menu then
			if l and not load_was then close_menu() else menu_input() end
		else
			if s and not save_was then do_save() end
			if l and not load_was then load_since = manager.machine.time:as_double() end
			if l and load_since and manager.machine.time:as_double() - load_since >= HOLD then
				load_since = nil
				open_menu()
			elseif not l and load_was and load_since then
				load_since = nil
				do_load(newest())
			end
		end
		save_was, load_was = s, l
	end

	-- for test scripts (-autoboot_script): savestates_api.save(), .load(n), .open(), .close(), .move(d), .slots()
	savestates_api = {
		save = function() do_save() end,
		load = function(n) do_load(n or newest()) end,
		open = function() open_menu() end,
		close = function() close_menu() end,
		move = function(d) if menu then menu.sel = (menu.sel - 1 + d) % #menu.entries + 1 end end,
		pick = function() if menu then pick() end end,
		slots = function() read_meta(); return slots, last end,
		state = function()   -- nil when closed, else boot flag, selection, entry count, selected entry (slot or 'fresh')
			if not menu then return nil end
			local e = menu.entries[menu.sel]
			return menu.boot, menu.sel, #menu.entries, e.fresh and 'fresh' or e.slot
		end,
	}

	reset_sub = emu.add_machine_reset_notifier(start)
	stop_sub = emu.add_machine_stop_notifier(function() close_menu(); sys = nil end)
	emu.register_periodic(poll)
	emu.register_frame_done(function() if menu then draw_menu() end end)
end

return exports
