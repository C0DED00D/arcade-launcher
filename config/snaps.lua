-- Used by the launcher to grab attract-mode snapshots for the menu (run with -video none -nothrottle).
-- Takes a snapshot at each of these emulated times (seconds), then exits.
local times = { 6, 12, 20, 30, 42, 56, 72, 90 }
local nexti = 1
-- must be global: a local subscription is garbage-collected and stops firing
snapshot_sub = emu.add_machine_frame_notifier(function()
    local t = manager.machine.time:as_double()
    if nexti <= #times and t >= times[nexti] then
        manager.machine.video:snapshot()
        nexti = nexti + 1
        if nexti > #times then manager.machine:exit() end
    end
end)

