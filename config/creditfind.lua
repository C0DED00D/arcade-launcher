-- Finds the RAM byte holding a game's credit count: snapshots the main CPU's RAM, inserts coins via the
-- "Coin 1" input, and reports bytes that went up by one per coin and stayed there.
-- env CF_T0 = seconds before the first coin (after boot tests), CF_COINS = number of coins (default 3).
if cf_loaded then return end
cf_loaded = true
local t0 = tonumber(os.getenv('CF_T0') or '20')
local coins = tonumber(os.getenv('CF_COINS') or '3')
local cpu = manager.machine.devices[':maincpu'] or manager.machine.devices[':mainpcb:maincpu']
local space = cpu.spaces['program']

-- RAM ranges from the CPU's memory map
local ranges = {}
for _, e in ipairs(space.map.entries) do
	local h = e.read.handlertype
	if h == 'ram' then table.insert(ranges, { e.address_start, e.address_end }) end
end

local function grab()
	local t = {}
	for _, r in ipairs(ranges) do
		for a = r[1], r[2] do t[a] = space:read_u8(a) end
	end
	return t
end

local coin
for _, port in pairs(manager.machine.ioport.ports) do
	for name, f in pairs(port.fields) do if name == 'Coin 1' then coin = f end end
end

local snaps = {}
local frame = 0
cf_sub = emu.add_machine_frame_notifier(function()
	frame = frame + 1
	local t = frame / 60
	for i = 0, coins do
		local ts = t0 + i * 3
		if math.abs(t - ts) < 0.5 / 60 then snaps[i] = grab() end
		if i < coins and t >= ts + 0.5 and t < ts + 0.6 then coin:set_value(1) elseif i < coins and t >= ts + 0.6 and t < ts + 0.7 then coin:clear_value() end
	end
	if t > t0 + coins * 3 + 1 then
		local hits = {}
		for a, v0 in pairs(snaps[0]) do
			local ok = true
			local exact = true
			for i = 1, coins do
				if snaps[i][a] ~= v0 + i then exact = false end
				if snaps[i][a] <= snaps[i - 1][a] then ok = false end
			end
			if ok then table.insert(hits, a) end
			if ok and not exact then
				local seq = {}
				for i = 0, coins do table.insert(seq, snaps[i][a]) end
				print(string.format('CF rising %X: %s', a, table.concat(seq, ',')))
			end
		end
		table.sort(hits)
		local rs = {}
		for _, r in ipairs(ranges) do table.insert(rs, string.format('%X-%X', r[1], r[2])) end
		print('CF ranges ' .. table.concat(rs, ' '))
		for _, a in ipairs(hits) do print(string.format('CF hit %s %X start=%d', cpu.tag, a, snaps[0][a])) end
		print('CF done ' .. #hits)
		manager.machine:exit()
	end
end)
