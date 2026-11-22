-- Drives a game's operator (service) menu from a step file, for setting free play.
-- Step file (path in env SVC_STEPS), one step per line, times in seconds of emulated time:
--   <t> press <field name>[|<field name>...] <seconds held>
--   <t> hold <field name> <seconds>      (same as press)
--   <t> snap
--   <t> exit
-- Field names are MAME input names, e.g. "Service Mode", "P1 Down", "P1 Button 1", "1 Player Start".
-- MAME re-runs the autoboot script after a soft reset (leaving a test menu resets many games): only start once.
if svc_loaded then return end
svc_loaded = true
local steps = {}
local f = io.open(os.getenv('SVC_STEPS'), 'r')
for line in f:lines() do
	local t, act, rest = line:match('^%s*([%d%.]+)%s+(%a+)%s*(.*)$')
	if t then
		local s = { t = tonumber(t), act = act }
		if act == 'press' or act == 'hold' then
			local names, dur = rest:match('^(.-)%s+([%d%.]+)$')
			s.names = {}
			for n in names:gmatch('[^|]+') do table.insert(s.names, n) end
			s.dur = tonumber(dur)
		end
		table.insert(steps, s)
	end
end
f:close()

local fields = {}
for _, port in pairs(manager.machine.ioport.ports) do
	for name, fld in pairs(port.fields) do
		if not fields[name] then fields[name] = fld end
	end
end

local frame = 0
local active = {}
svc_sub = emu.add_machine_frame_notifier(function()
	frame = frame + 1
	local now = frame / 60
	for i = #active, 1, -1 do
		if now >= active[i].until_t then active[i].fld:clear_value(); table.remove(active, i) end
	end
	for _, a in ipairs(active) do a.fld:set_value(1) end
	while #steps > 0 and now >= steps[1].t do
		local s = table.remove(steps, 1)
		if s.act == 'snap' then
			manager.machine.video:snapshot()
		elseif s.act == 'exit' then
			manager.machine:exit()
		elseif s.act == 'reset' then
			manager.machine:soft_reset()
		else
			for _, n in ipairs(s.names) do
				local fld = fields[n]
				if fld then
					fld:set_value(1)
					table.insert(active, { fld = fld, until_t = now + s.dur })
				else
					print('svcdrive: no field ' .. n)
				end
			end
		end
	end
end)
