-- Arcade launcher: yoke calibration at boot.
-- Some analog-yoke games learn the yoke's range from the furthest positions they see after power-on and start
-- with a guess that doesn't match MAME's centre, so a centred stick reads as off to one side until the player has
-- swung the yoke to every end. This plugin does that for them: for 4 seconds early in each (re)start it holds
-- the P1 analog stick in one corner and then the opposite one, then hands it back at centre.
-- Which games: local.lua beside this file, returning { games = { '<set>', ... } } (parent names cover clones).
local exports = {
	name = 'yokecal',
	version = '1.0',
	description = 'Arcade launcher: teach self-calibrating yoke games the full stick range at boot',
	license = 'BSD-3-Clause',
	author = { name = 'Arcade launcher' } }

local yokecal = exports
local frame_sub, reset_sub

-- Emulated seconds after (re)start. The games only start learning once they're running (about 4 s in), then widen
-- their range by roughly 70 units a second towards whatever they see: LOW seconds at the bottom-left corner,
-- then HIGH seconds at the top-right one teaches both ends (measured on the Star Wars board, 2026-11-25).
local FROM, LOW, HIGH = 4.5, 1.8, 2.2

local GAMES = { }
do
	local ok, t = pcall(function()
		local p = manager.plugins['yokecal']
		return dofile(((p and p.directory) or 'plugins/yokecal') .. '/local.lua')
	end)
	if ok and type(t) == 'table' then
		for _, g in ipairs(t.games or { }) do GAMES[g] = true end
	elseif not ok and not tostring(t):find('No such file') and not tostring(t):find('cannot open') then
		emu.print_error('yokecal: local.lua: ' .. tostring(t))
	end
end

function yokecal.startplugin()
	local fx, fy, t0, done

	local function start()
		fx, fy, done = nil, nil, true
		local sys = manager.machine.system
		if not (GAMES[sys.name] or (sys.parent and GAMES[sys.parent])) then return end
		local ioport = manager.machine.ioport
		local tx, ty = ioport:token_to_input_type('P1_AD_STICK_X'), ioport:token_to_input_type('P1_AD_STICK_Y')
		for _, port in pairs(ioport.ports) do
			for _, f in pairs(port.fields) do
				if f.type == tx then fx = f elseif f.type == ty then fy = f end
			end
		end
		if not (fx and fy) then emu.print_error('yokecal: no P1 analog stick'); return end
		t0 = manager.machine.time:as_double()
		done = false
		emu.print_info('yokecal: sweeping the yoke range')
	end

	local function frame()
		if done ~= false then return end
		local t = manager.machine.time:as_double() - t0
		if t < FROM then return end
		if t < FROM + LOW then
			fx:set_value(fx.minvalue); fy:set_value(fy.minvalue)
		elseif t < FROM + LOW + HIGH then
			fx:set_value(fx.maxvalue); fy:set_value(fy.maxvalue)
		else
			-- (set_value latches an analog field until cleared: hand it back to the real stick)
			fx:clear_value(); fy:clear_value()
			done = true
			emu.print_info('yokecal: done')
		end
	end

	frame_sub = emu.add_machine_frame_notifier(frame)
	reset_sub = emu.add_machine_reset_notifier(start)
end

return exports
