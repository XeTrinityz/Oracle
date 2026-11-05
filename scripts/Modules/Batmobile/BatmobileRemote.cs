using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;
using SelfDrive = BmSDK.BmGame.RVehicle.ESelfDriveMode;

namespace CeZeY.Oracle;

/// <summary>
/// Remote control without the gadget and its restrictions. RPlayerController.RemoteControlBatmobile with bForceSwitch
/// skips CanCallBatmobile (no-call areas, EMP); it still refuses while Batman is attacked or chased unless the
/// area's AffectPlayerInfo.CanUseRemoteControlInCombat is set, which Oracle sets for that one call. A fight then
/// ends the remote drive unless the car's DoNotBreakRemoteControlIfBatmanInCombat is set. The range limit is the
/// edge of the city loaded around Batman (GRI.GetDistanceToVisibleStreamingBoundary), so it stays.
/// </summary>
internal sealed partial class Batmobile
{
    private readonly Held<RVehicleBatmobileBase, bool> _remoteKeeps = new("remote control in fights",
        b => b.DoNotBreakRemoteControlIfBatmanInCombat, (b, v) => b.DoNotBreakRemoteControlIfBatmanInCombat = v);

    private Toggle _remoteInFights = null!;

    private void BuildRemote()
    {
        AddCommand("remote_control", "REMOTE CONTROL BATMOBILE", "Starts driving the Batmobile by remote without the gadget, even where the game won't let you call it. Run it again to stop. If the car is out of range, the game brings it closer first, as the gadget does.",
            ToggleRemote);
        _remoteInFights = AddToggle("remote_in_fights", "REMOTE CONTROL IN FIGHTS", "Enemies going for Batman no longer cut the remote control off, and REMOTE CONTROL BATMOBILE works mid-fight.",
            on => { if (!on) _remoteKeeps.Restore(); }).Maintain(() => { if (Car() is { } car) _remoteKeeps.Set(car, true); });
    }

    private void ToggleRemote()
    {
        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        if (pc.IsBatmobileRemoteDrive())
        {
            if (World.Pawn is not RVehicleBatmobileBase driven)
                throw new InvalidOperationException("the remote drive is already ending");
            if (!driven.DriverLeave(true, false))
                throw new InvalidOperationException("the game wouldn't end the remote drive");
            return;
        }

        if (World.Pawn is RVehicleBatmobileBase)
            throw new InvalidOperationException("you are sitting in the Batmobile");
        if (World.Pawn is not RPawnPlayer player)
            throw new InvalidOperationException("the game is still loading");
        if (!Teleport.InCityMap(player.Location))
            throw new InvalidOperationException("the remote doesn't reach from indoors");

        RVehicleBatmobileBase car = Car() ?? throw new InvalidOperationException("there's no Batmobile in this area");
        // A car the story is holding or driving stays the story's.
        if (car.IsStolen || car.bControlledBySeqAct || car.SelfDriveMode != SelfDrive.SELFDRIVE_None)
            throw new InvalidOperationException("the game is using the Batmobile right now");

        bool callable = Safe.Try("call check", pc.CanCallBatmobile);
        ref var area = ref pc.AffectPlayerInfo;
        bool combatAllowed = area.CanUseRemoteControlInCombat;
        bool started;
        try
        {
            if (_remoteInFights.On)
                area.CanUseRemoteControlInCombat = true;
            started = pc.RemoteControlBatmobile(car, true);
        }
        finally
        {
            area.CanUseRemoteControlInCombat = combatAllowed;
        }

        Log.Info("REMOTE CONTROL: the game " + (started ? "started" : "refused") + " the remote drive (the gadget could " + (callable ? "" : "not ") + "call the car here).");
        if (!started)
        {
            throw new InvalidOperationException(_remoteInFights.On
                ? "the game refused, Batman may be mid-move or going through a door"
                : "the game refused, probably because Batman is under attack (see REMOTE CONTROL IN FIGHTS)");
        }
    }
}
