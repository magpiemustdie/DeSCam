$anchor_dir  = 0x1B8
$anchor_node = 0x0870

$derived = @(
    [pscustomobject]@{n="TransChaseByControlEndWaitTime"; dir=0x1BC; code=0x08C0},
    [pscustomobject]@{n="TransChaseByControlEndRelTime";  dir=0x1C0; code=0x0910},
    [pscustomobject]@{n="RotCtrlEndWaitTime";             dir=0x1C4; code=0x0960},
    [pscustomobject]@{n="RotCtrlEndReleaseTime";          dir=0x1C8; code=0x09B0},
    [pscustomobject]@{n="RotHiSpeedRateIncTime";          dir=0x1CC; code=0x0A00},
    [pscustomobject]@{n="RotRange_MaxX";                  dir=0x1D0; code=0x0A50},
    [pscustomobject]@{n="RotRange_MinX";                  dir=0x1D4; code=0x0AA0},
    [pscustomobject]@{n="RotRangeAtLock_MaxX";            dir=0x1D8; code=0x0AF0},
    [pscustomobject]@{n="RotRangeAtLock_MinX";            dir=0x1DC; code=0x0B40},
    [pscustomobject]@{n="RotRangeLerpHeight_Begin";       dir=0x1E0; code=0x0B90},
    [pscustomobject]@{n="RotRangeLerpHeight_End";         dir=0x1E4; code=0x0BE0},
    [pscustomobject]@{n="RotSpeed_X";                     dir=0x1E8; code=0x0C30},
    [pscustomobject]@{n="RotSpeed_Y";                     dir=0x1EC; code=0x0C80},
    [pscustomobject]@{n="RotSpeedMin_X";                  dir=0x1F0; code=0x0CD0},
    [pscustomobject]@{n="RotSpeedMin_Y";                  dir=0x1F4; code=0x0D20},
    [pscustomobject]@{n="RotSpeedMax_X";                  dir=0x1F8; code=0x0D70},
    [pscustomobject]@{n="RotSpeedMax_Y";                  dir=0x1FC; code=0x0DC0},
    [pscustomobject]@{n="RotHiSpeed_X";                   dir=0x200; code=0x0E10},
    [pscustomobject]@{n="RotHiSpeed_Y";                   dir=0x204; code=0x0E60},
    [pscustomobject]@{n="RotHiSpeedMin_X";                dir=0x208; code=0x0EB0},
    [pscustomobject]@{n="RotHiSpeedMin_Y";                dir=0x20C; code=0x0F00},
    [pscustomobject]@{n="RotHiSpeedMax_X";                dir=0x210; code=0x0F50},
    [pscustomobject]@{n="RotHiSpeedMax_Y";                dir=0x214; code=0x0FA0},
    [pscustomobject]@{n="OptionRotSpeedRate";             dir=0x218; code=0x0FF0},
    [pscustomobject]@{n="DirectPitchAng";                 dir=0x21C; code=0x1040},
    [pscustomobject]@{n="DirectPitchRate";                dir=0x220; code=0x1090},
    [pscustomobject]@{n="LockRotChaseRate";               dir=0x224; code=0x10E0},
    [pscustomobject]@{n="LockRotChasePlayAng";            dir=0x228; code=0x1130},
    [pscustomobject]@{n="LockRotXShiftRatio";             dir=0x22C; code=0x1180},
    [pscustomobject]@{n="ForceFrontLockTime";             dir=0x248; code=0x13B0},
    [pscustomobject]@{n="EscapeWallRotAddX";              dir=0x260; code=0x1590},
    [pscustomobject]@{n="EscapeWallRotAddY";              dir=0x264; code=0x15E0},
    [pscustomobject]@{n="EscapeWallRotMaxX";              dir=0x268; code=0x1630},
    [pscustomobject]@{n="EscapeWallRotMaxY";              dir=0x26C; code=0x1680},
    [pscustomobject]@{n="EscapeWallRotDampRatio";         dir=0x270; code=0x16D0},
    [pscustomobject]@{n="EscapeWallWingAngY";             dir=0x274; code=0x1720},
    [pscustomobject]@{n="EscapeWallWingLen";              dir=0x278; code=0x1770},
    [pscustomobject]@{n="EscapeWallWingRadius";           dir=0x27C; code=0x17C0},
    [pscustomobject]@{n="EscapeWallWingSeparate";         dir=0x280; code=0x1810},
    [pscustomobject]@{n="CamModeParamLerpRate";           dir=0x294; code=0x19A0}
)

$allOk = $true
foreach ($p in $derived) {
    $slots    = ($p.dir - $anchor_dir) / 4
    $expected = $anchor_node + $slots * 0x50
    if ($expected -eq $p.code) {
        Write-Host ("OK   slots={0,3}  dir=0x{1:X3}  node=0x{2:X4}  {3}" -f $slots, $p.dir, $expected, $p.n) -ForegroundColor Green
    } else {
        Write-Host ("FAIL slots={0,3}  dir=0x{1:X3}  expected=0x{2:X4}  got=0x{3:X4}  {4}" -f $slots, $p.dir, $expected, $p.code, $p.n) -ForegroundColor Red
        $allOk = $false
    }
}
Write-Host ""
if ($allOk) { Write-Host "ALL CORRECT" -ForegroundColor Green }
else        { Write-Host "ERRORS — see above" -ForegroundColor Red }
