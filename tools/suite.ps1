# Co-op test suite. Dot-source coop.ps1 first. Each test prints PASS/FAIL with evidence.
. "$PSScriptRoot\coop.ps1"

$Script:Results = [System.Collections.Generic.List[string]]::new()
function Report($name, [bool]$ok, $detail) {
    $line = "{0} {1} - {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail
    $Script:Results.Add($line); $line
}
function Money($pc) { (Get-State $pc).money }

# Base spot: the open starting beach.
$Beach = @(8, 0.6, -234)

function Test-CoinScenario($name, $dropper, $value, [double[]]$aPos, [double[]]$bPos, $dropDist) {
    Do-Cmd A ("tp {0} {1} {2}" -f $aPos[0], $aPos[1], $aPos[2]) 5 | Out-Null
    Do-Cmd B ("tp {0} {1} {2}" -f $bPos[0], $bPos[1], $bPos[2]) 5 | Out-Null
    Start-Sleep 2
    $a0 = Money A; $b0 = Money B
    $r = Do-Cmd $dropper "spawncoins $value $dropDist" 5
    Start-Sleep 1
    $seenA = Do-Cmd A 'coins 30' 5; $seenB = Do-Cmd B 'coins 30' 5
    Start-Sleep 7
    $a1 = Money A; $b1 = Money B
    $ok = ($a0 -eq $b0) -and ($a1 -eq $b1) -and ($a1 -eq $a0 + $value)
    Report "coins:$name" $ok "start A=$a0 B=$b0 -> end A=$a1 B=$b1 (want $($a0 + $value)); seenA=[$(($seenA -split '\| ok \| ')[1])] seenB=[$(($seenB -split '\| ok \| ')[1])]"
}

function Test-Chest {
    Do-Cmd A 'god on' 5 | Out-Null; Do-Cmd B 'god on' 5 | Out-Null
    Do-Cmd A 'tp 96.5 28 -135.3' 5 | Out-Null; Do-Cmd B 'tp 93 28 -133.5' 5 | Out-Null
    Start-Sleep 2
    $before = (Do-Cmd B 'chests 25' 5); $a0 = Money A; $b0 = Money B
    Do-Cmd A 'face 96.5 -137' 5 | Out-Null
    Do-Cmd A 'press InteractDodge' 5 | Out-Null
    Start-Sleep 10
    $afterA = Do-Cmd A 'chests 25' 5; $afterB = Do-Cmd B 'chests 25' 5
    $a1 = Money A; $b1 = Money B
    $flagKeys = Get-Log B -Pattern "\[world\] remote set" -Last 8
    $ok = ($a1 -eq $b1) -and ($a1 -ge $a0 + 25)
    Report 'chest:money' $ok "A $a0->$a1, B $b0->$b1 (want equal, >= +25; bloodstain pickups can add more)"
    Report 'chest:flag-on-B' ([bool]($flagKeys | Where-Object { $_ -notmatch 'hp_lost' })) ("B remote keys: " + (($flagKeys | ForEach-Object { ($_ -split '\] ')[-1] }) -join ' ; '))
    Report 'chest:shown-open-B' ($afterB -match '\|15\|[^|]*\|1\|') "B: $(($afterB -split '\| ok \| ')[1])"
    Report 'chest:key-on-B' ((Do-Cmd B 'getint chest open 15' 5) -match '=1') 'chest open 15 on B'
    Get-Shot B 'chest_B' | Out-Null
}

# Chest 90 at (26,28,-116), contents Pepper x2, opened from its south side. (Chest 15 had a bloodstain
# chest next to it from an earlier death, which grabbed the interaction.)
function Test-Chest90 {
    Do-Cmd A 'god on' 5 | Out-Null; Do-Cmd B 'god on' 5 | Out-Null
    Do-Cmd A 'tp 26 28 -117.6' 5 | Out-Null; Do-Cmd B 'tp 24 28 -118' 5 | Out-Null
    Start-Sleep 2
    $pa0 = (Do-Cmd A 'items' 5); $pb0 = (Do-Cmd B 'items' 5)
    $kb0 = Do-Cmd B 'getint chest open 90' 5
    Do-Cmd A 'face 26 -116' 5 | Out-Null
    Do-Cmd A 'press InteractDodge' 5 | Out-Null
    Start-Sleep 9
    $ka = Do-Cmd A 'getint chest open 90' 5; $kb = Do-Cmd B 'getint chest open 90' 5
    $cb = Do-Cmd B 'chests 8' 5
    Do-Cmd A 'press InteractDodge' 5 | Out-Null      # dismiss the item presentation (item is granted after it)
    Start-Sleep 3
    $pa1 = (Do-Cmd A 'items' 5); $pb1 = (Do-Cmd B 'items' 5)
    Report 'chest90:opened-on-A' ($ka -match '=1') $ka
    Report 'chest90:key-on-B' (($kb -match '=1') -and ($kb0 -notmatch '=1')) "before: $kb0 | after: $kb"
    Report 'chest90:shown-open-B' ($cb -match '\|90\|[^|]*\|1\|') "B: $(($cb -split '\| ok \| ')[1])"
    $ia1 = ($pa1 -split '\| ok \| ')[1] -replace ' \| t=.*',''; $ib1 = ($pb1 -split '\| ok \| ')[1] -replace ' \| t=.*',''
    $ia0 = ($pa0 -split '\| ok \| ')[1] -replace ' \| t=.*',''
    Report 'chest90:items' (($ia1 -eq $ib1) -and ($ia1 -ne $ia0)) "inventories equal after open and changed: A=[$ia1] B=[$ib1]"
    Get-Shot B 'chest90_B' | Out-Null
}

# Parse "mons" output → objects (id, type, x, z, hp, dead, mirrored)
function Get-Mons($pc, $radius = 25) {
    $r = Do-Cmd $pc "mons $radius" 5
    $body = ($r -split '\| ok \| ')[1]
    $list = @()
    foreach ($m in ([regex]::Matches($body, '(\-?\d+):(\w+)@(\-?[\d\.]+),(\-?[\d\.]+) hp=(\-?\d+) dead=(\d) own=(\d+)( M)?'))) {
        $list += [pscustomobject]@{ Id = [int]$m.Groups[1].Value; Type = $m.Groups[2].Value; X = [double]$m.Groups[3].Value; Z = [double]$m.Groups[4].Value
                                   Hp = [int]$m.Groups[5].Value; Dead = $m.Groups[6].Value -eq '1'; Owner = [int]$m.Groups[7].Value; Mirror = $m.Groups[8].Success }
    }
    [pscustomobject]@{ Raw = $body; List = $list }
}

# Simultaneous snapshot from both PCs (send both, then wait both) so moving enemies compare fairly.
function Get-MonsBoth($radius = 30) {
    $sa = Send-Cmd A "mons $radius"; $sb = Send-Cmd B "mons $radius"
    $ra = Wait-Seq A $sa 10; $rb = Wait-Seq B $sb 10
    $parse = { param($r) $body = ($r -split '\| ok \| ')[1]; $l = @()
        foreach ($m in ([regex]::Matches($body, '(\-?\d+):(\w+)@(\-?[\d\.]+),(\-?[\d\.]+) hp=(\-?\d+) dead=(\d) own=(\d+)( M)?'))) {
            $l += [pscustomobject]@{ Id = [int]$m.Groups[1].Value; Type = $m.Groups[2].Value; X = [double]$m.Groups[3].Value; Z = [double]$m.Groups[4].Value
                                    Hp = [int]$m.Groups[5].Value; Dead = $m.Groups[6].Value -eq '1'; Owner = [int]$m.Groups[7].Value; Mirror = $m.Groups[8].Success } }
        [pscustomobject]@{ Raw = $body; List = $l } }
    [pscustomobject]@{ A = (& $parse $ra); B = (& $parse $rb) }
}

function DeathsOn($pc) { @(Get-Log $pc -Pattern '\[mon\] \d+ died \(owner\)' -Last 300 | ForEach-Object { [regex]::Match($_, '\[mon\] (\d+) died').Groups[1].Value }) }
function AllOwnerDeaths { @(DeathsOn A) + @(DeathsOn B) }   # @() each: a single result unrolls to a string and + would concatenate ids
function OtherSawDeath($id) {
    [bool]((Get-Log A -Pattern "\[mon\] $id died \(owner said\)" -Last 80) + (Get-Log B -Pattern "\[mon\] $id died \(owner said\)" -Last 80))
}

# Attack the nearest live Blob from $pc's point of view until a new owner-side death appears (either PC).
function Kill-Nearest($pc, $maxSwings = 16) {
    $before = AllOwnerDeaths
    for ($i = 0; $i -lt $maxSwings; $i++) {
        $me = (Get-State $pc).player.pos
        if (-not $me) { Start-Sleep -Milliseconds 300; continue }   # state read mid-write
        $live = (Get-Mons $pc 30).List | Where-Object { $_.Type -eq 'Blob' -and -not $_.Dead } |
                Sort-Object { ($_.X - $me[0]) * ($_.X - $me[0]) + ($_.Z - $me[2]) * ($_.Z - $me[2]) }
        $t = $live | Select-Object -First 1
        if (-not $t) { break }
        Do-Cmd $pc ("moveto {0} {1} 2.5 1.2" -f $t.X, $t.Z) 6 | Out-Null
        Do-Cmd $pc ("face {0} {1}" -f $t.X, $t.Z) 5 | Out-Null
        Do-Cmd $pc 'press Item1' 5 | Out-Null
        Start-Sleep -Milliseconds 300
        $new = @(AllOwnerDeaths | Where-Object { $before -notcontains $_ })
        if ($new.Count -gt 0) { return @{ Id = $new[0]; Swings = $i + 1 } }
    }
    @{ Id = $null; Swings = $maxSwings }
}

function Test-Combat {
    Do-Cmd A 'god on' 5 | Out-Null; Do-Cmd B 'god on' 5 | Out-Null
    Do-Cmd A 'tp 93 28 -141' 5 | Out-Null; Do-Cmd B 'tp 89 28 -139' 5 | Out-Null   # open floor among the slimes
    Start-Sleep 5
    $both = Get-MonsBoth 30
    $shared = foreach ($m in $both.A.List) { $o = $both.B.List | Where-Object { $_.Id -eq $m.Id }; if ($o -and -not $m.Dead) { [pscustomobject]@{ Id = $m.Id; OwnA = $m.Owner; OwnB = $o.Owner; MirA = $m.Mirror; MirB = $o.Mirror; DX = $m.X - $o.X; DZ = $m.Z - $o.Z } } }
    $agree = @($shared | Where-Object { $_.OwnA -eq $_.OwnB }).Count
    $mirrorOk = @($shared | Where-Object { ($_.OwnA -eq 0 -and $_.MirB -and -not $_.MirA) -or ($_.OwnA -eq 1 -and $_.MirA -and -not $_.MirB) }).Count
    $owners = ($shared | Group-Object OwnA | ForEach-Object { "p$($_.Name)=$($_.Count)" }) -join ' '
    Report 'combat:ownership' (@($shared).Count -gt 0 -and $agree -eq @($shared).Count -and $mirrorOk -eq @($shared).Count) "$(@($shared).Count) shared monsters, owners agree on $agree, mirror roles right on $mirrorOk ($owners)"
    $maxd = ($shared | ForEach-Object { [Math]::Sqrt($_.DX * $_.DX + $_.DZ * $_.DZ) } | Measure-Object -Maximum).Maximum
    Report 'combat:mirror-positions' ($maxd -lt 2.0) ("max A-vs-B distance {0:F2} m (same-moment query)" -f $maxd)
    $kb = Kill-Nearest B
    Start-Sleep 2
    Report 'combat:B-kills' ([bool]$kb.Id -and (OtherSawDeath $kb.Id)) "monster $($kb.Id) in $($kb.Swings) swings; other side saw death=$(OtherSawDeath $kb.Id)"
    $ka = Kill-Nearest A
    Start-Sleep 2
    Report 'combat:A-kills' ([bool]$ka.Id -and (OtherSawDeath $ka.Id)) "monster $($ka.Id) in $($ka.Swings) swings; other side saw death=$(OtherSawDeath $ka.Id)"
    $ma = Money A; $mb = Money B
    Report 'combat:money-equal' ($ma -eq $mb) "A=$ma B=$mb"
}

function Test-Aggro {
    Do-Cmd A 'god on' 5 | Out-Null; Do-Cmd B 'god off' 5 | Out-Null
    Do-Cmd A 'tp 70 20.5 -138' 5 | Out-Null          # owner ~20 m away, lower level
    Do-Cmd B 'tp 90 28 -140' 5 | Out-Null            # B among the slimes
    $hp0 = (Get-State B).player.hp
    Start-Sleep 10
    $bpos = (Get-State B).player.pos; $hp1 = (Get-State B).player.hp
    $both = Get-MonsBoth 40
    $nearB = @($both.B.List | Where-Object { -not $_.Dead -and [Math]::Sqrt(($_.X - $bpos[0]) * ($_.X - $bpos[0]) + ($_.Z - $bpos[2]) * ($_.Z - $bpos[2])) -lt 12 })
    $ownedByB = @($nearB | Where-Object { $_.Owner -eq 1 }).Count
    Report 'aggro:handed-to-B' ($nearB.Count -gt 0 -and $ownedByB -eq $nearB.Count) "$ownedByB/$($nearB.Count) monsters within 12 m of B are owned by B; $($both.B.Raw.Substring(0, [Math]::Min(70, $both.B.Raw.Length)))"
    $closest = ($nearB | ForEach-Object { [Math]::Sqrt(($_.X - $bpos[0]) * ($_.X - $bpos[0]) + ($_.Z - $bpos[2]) * ($_.Z - $bpos[2])) } | Measure-Object -Minimum).Minimum
    Report 'aggro:slimes-on-B' ($closest -lt 3) ("nearest slime to B: {0:F1} m" -f $closest)
    Report 'aggro:B-takes-damage' ($hp1 -lt $hp0) "B hp $hp0 -> $hp1"
    Get-Shot B 'aggro_B' | Out-Null
    Do-Cmd B 'god on' 5 | Out-Null
}
function Puppet($pc) { (Get-State $pc).puppets | Select-Object -First 1 }

# Walk a PC into a portal: approach, then keep pushing toward it until the scene changes.
function Enter-Portal($pc, [double]$x, [double]$z, $toScene, $timeout = 25) {
    Do-Cmd $pc ("moveto {0} {1} 8 0.4" -f $x, $z) 12 | Out-Null
    $r = Do-Cmd $pc "waitscene $toScene $timeout" ($timeout + 5)
    if ($r -notmatch ' ok ') {   # nudge through the trigger
        $p = (Get-State $pc).player.pos
        Do-Cmd $pc ("moveto {0} {1} 4 0.2" -f ($x + ($x - $p[0])), ($z + ($z - $p[2]))) 8 | Out-Null
        $r = Do-Cmd $pc "waitscene $toScene $timeout" ($timeout + 5)
    }
    Do-Cmd $pc 'waitplayer 40' 45 | Out-Null
    $r
}

function Test-Scenes {
    # Deterministic start: saves may have been autosaved inside the cave by an earlier run.
    Do-Cmd A 'goto Overworld Redux' 50 | Out-Null; Do-Cmd B 'goto Overworld Redux' 50 | Out-Null
    Do-Cmd A 'waitplayer 40' 45 | Out-Null; Do-Cmd B 'waitplayer 40' 45 | Out-Null
    Do-Cmd A 'god on' 5 | Out-Null; Do-Cmd B 'god on' 5 | Out-Null
    Do-Cmd A 'tp -11 12.6 -121' 5 | Out-Null; Do-Cmd B 'tp -8 12.6 -121' 5 | Out-Null
    Start-Sleep 3
    Report 'scenes:together-before' ((Puppet A).active -and (Puppet B).active) "A sees B=$((Puppet A).active) B sees A=$((Puppet B).active)"

    $r = Enter-Portal A -11 -126.75 'Sword Cave'
    Start-Sleep 3
    $sa = Get-State A; $sb = Get-State B
    Report 'scenes:A-entered' ($sa.scene -eq 'Sword Cave') "A scene=$($sa.scene) ($r)"
    Report 'scenes:split-hidden' ((-not (Puppet A).active) -and (-not (Puppet B).active)) "A sees B=$((Puppet A).active) B sees A=$((Puppet B).active)"
    $mb = (Get-Mons B 400).Raw
    Report 'scenes:B-took-overworld-monsters' ($mb -match 'owned=[1-9]' -and $mb -match 'mirrored=0') ($mb.Substring(0, [Math]::Min(60, $mb.Length)))

    $r = Enter-Portal B -11 -126.75 'Sword Cave'
    Start-Sleep 4
    $sa = Get-State A; $sb = Get-State B; $pa = Puppet A; $pb = Puppet B
    $dAB = [Math]::Sqrt([Math]::Pow($pa.pos[0] - $sb.player.pos[0], 2) + [Math]::Pow($pa.pos[2] - $sb.player.pos[2], 2))
    $dBA = [Math]::Sqrt([Math]::Pow($pb.pos[0] - $sa.player.pos[0], 2) + [Math]::Pow($pb.pos[2] - $sa.player.pos[2], 2))
    Report 'scenes:B-entered' ($sb.scene -eq 'Sword Cave') "B scene=$($sb.scene) ($r)"
    Report 'scenes:together-inside' ($pa.active -and $pb.active -and $dAB -lt 0.6 -and $dBA -lt 0.6) ("visible both ways; puppet error A->B {0:F2} m, B->A {1:F2} m" -f $dAB, $dBA)
    Report 'scenes:no-stray-fx' ($pa.renderersOn.emitting -le 3 -and $pb.renderersOn.emitting -le 3) "emitting A=$($pa.renderersOn.emitting) B=$($pb.renderersOn.emitting); renderers A=$($pa.renderersOn.renderers) B=$($pb.renderersOn.renderers)"
    Get-Shot A 'cave_A' | Out-Null; Get-Shot B 'cave_B' | Out-Null

    $exit = ((Do-Cmd A 'portals 40' 5) -split '\| ok \| ')[1] -replace ' \| t=.*','' -split ';' | Where-Object { $_ -match 'Overworld' } | Select-Object -First 1
    if ($exit) {
        $e = $exit -split '\|'
        $r = Enter-Portal A ([double]$e[0]) ([double]$e[2]) 'Overworld Redux'
        Start-Sleep 3
        Report 'scenes:A-left' ((Get-State A).scene -eq 'Overworld Redux' -and -not (Puppet A).active -and -not (Puppet B).active) "A scene=$((Get-State A).scene); A sees B=$((Puppet A).active) B sees A=$((Puppet B).active)"
    } else { Report 'scenes:A-left' $false 'no exit portal found in Sword Cave' }
}

function Test-Death {
    Do-Cmd A 'goto Overworld Redux' 50 | Out-Null; Do-Cmd B 'goto Overworld Redux' 50 | Out-Null
    Do-Cmd A 'waitplayer 40' 45 | Out-Null; Do-Cmd B 'waitplayer 40' 45 | Out-Null
    Do-Cmd A 'god on' 5 | Out-Null; Do-Cmd B 'god off' 5 | Out-Null
    Do-Cmd A 'tp 70 20.5 -138' 5 | Out-Null; Do-Cmd B 'tp 90 28 -140' 5 | Out-Null
    $m0a = Money A; $m0b = Money B
    $died = $false; $deathPos = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep 2; $st = Get-State B; $hp = $st.player.hp; if ($st.player) { $deathPos = $st.player.pos }
        if ($hp -le 0 -or $null -eq $hp) { $died = $true; break }
        # A is alive, so B goes DOWN first (revive system); give up = hold F2 → real death, which is what this test covers
        if ((Revive-State B) -match 'downed=True') { Do-Cmd B 'revive giveup' 5 | Out-Null }
    }
    Report 'death:B-died' $died "B hp reached 0 after ~$(2 * ($i + 1)) s"
    # Death → fade → reload at the campfire. Wait for the reload to START, then for the player again.
    for ($j = 0; $j -lt 20; $j++) { if ((Get-State B).scene -eq 'Loading') { break }; Start-Sleep 1 }
    # A player's FIRST death plays TUNIC's 'Resurrection' scene, which waits for input: advance it.
    for ($j = 0; $j -lt 30; $j++) {
        $sc = (Get-State B).scene
        if ($sc -eq 'Resurrection') { Do-Cmd B 'press InteractDodge' 5 | Out-Null; Start-Sleep 2; continue }
        if ($sc -ne 'Loading' -and $j -gt 2) { break }
        Start-Sleep 2
    }
    $r = Do-Cmd B 'waitplayer 60' 70
    Start-Sleep 3
    $sb = Get-State B; $sa = Get-State A
    # First death of a save plays TUNIC's 'Resurrection' sequence (a walk-through scene for the player); accept it.
    $firstDeath = $sb.scene -eq 'Resurrection'
    Report 'death:B-respawned' ($sb.player.hp -gt 0 -or $firstDeath) "B scene=$($sb.scene) hp=$($sb.player.hp) pos=$(($sb.player.pos | % { [math]::Round($_, 1) }) -join ',') ($r)"
    Report 'death:still-connected' ($sa.net.players.Count -eq 2 -and $sb.net.players.Count -eq 2) "players A=$($sa.net.players.Count) B=$($sb.net.players.Count)"
    $pa = Puppet A
    if ($sa.scene -eq $sb.scene) {
        $d = [Math]::Sqrt([Math]::Pow($pa.pos[0] - $sb.player.pos[0], 2) + [Math]::Pow($pa.pos[2] - $sb.player.pos[2], 2))
        Report 'death:puppet-follows' ($pa.active -and $d -lt 1) ("A's puppet of B active=$($pa.active) error {0:F2} m" -f $d)
    } else { Report 'death:puppet-follows' (-not $pa.active) "different scenes (A=$($sa.scene) B=$($sb.scene)); puppet hidden=$(-not $pa.active)" }
    Start-Sleep 2
    $m1a = Money A; $m1b = Money B
    Report 'death:money-equal' ($m1a -eq $m1b) "money before A=$m0a B=$m0b, after A=$m1a B=$m1b"
    # Recover the bloodstain (dropped money) at the death spot: everyone should get it back.
    Do-Cmd B 'god on' 5 | Out-Null
    if ($deathPos -and -not $firstDeath) {
        Do-Cmd B ("tp {0} {1} {2}" -f $deathPos[0], $deathPos[1], ($deathPos[2] + 1.6)) 5 | Out-Null
        Do-Cmd B ("face {0} {1}" -f $deathPos[0], $deathPos[2]) 5 | Out-Null
        Do-Cmd B 'press InteractDodge' 5 | Out-Null
        Start-Sleep 5
        $m2a = Money A; $m2b = Money B
        Report 'death:bloodstain-recovered' (($m2a -eq $m2b) -and ($m2a -gt $m1a)) "money after recovery A=$m2a B=$m2b (was $m1a)"
    }
}

function Test-ThreePlayers {
    if (-not (Test-Running C)) { Update-Game C | Out-Null }
    $r = Do-Cmd C 'newgame 60' 90
    $r2 = Do-Cmd C 'waitplayer 90' 100
    Report '3p:C-in-game' ($r2 -match ' ok ') "$r | $r2"
    Do-Cmd C 'join' 10 | Out-Null
    $w = Do-Cmd A 'waitplayers 3 30' 40
    Start-Sleep 3
    $s = @{ A = (Get-State A); B = (Get-State B); C = (Get-State C) }
    Report '3p:all-see-3' ((@($s.A.net.players).Count -eq 3) -and (@($s.B.net.players).Count -eq 3) -and (@($s.C.net.players).Count -eq 3)) "players A=$(@($s.A.net.players).Count) B=$(@($s.B.net.players).Count) C=$(@($s.C.net.players).Count) ($w)"
    # Everyone to the beach so all puppets share a scene.
    foreach ($p in 'A', 'B', 'C') { Do-Cmd $p 'goto Overworld Redux' 50 | Out-Null }
    foreach ($p in 'A', 'B', 'C') { Do-Cmd $p 'waitplayer 40' 45 | Out-Null; Do-Cmd $p 'god on' 5 | Out-Null }
    Do-Cmd A 'tp 8 0.6 -234' 5 | Out-Null; Do-Cmd B 'tp 11 0.6 -234' 5 | Out-Null; Do-Cmd C 'tp 14 0.6 -233' 5 | Out-Null
    Start-Sleep 3
    $s = @{ A = (Get-State A); B = (Get-State B); C = (Get-State C) }
    $ids = @{ A = $s.A.net.localId; B = $s.B.net.localId; C = $s.C.net.localId }
    $worst = 0; $seen = @()
    foreach ($viewer in 'A', 'B', 'C') {
        foreach ($other in 'A', 'B', 'C') {
            if ($viewer -eq $other) { continue }
            $pp = $s[$viewer].puppets | Where-Object { $_.id -eq $ids[$other] } | Select-Object -First 1
            if (-not $pp -or -not $pp.active) { $seen += "$viewer-x->$other"; $worst = 99; continue }
            $d = [Math]::Sqrt([Math]::Pow($pp.pos[0] - $s[$other].player.pos[0], 2) + [Math]::Pow($pp.pos[2] - $s[$other].player.pos[2], 2))
            $worst = [Math]::Max($worst, $d); $seen += ("{0}->{1} {2:F2}m" -f $viewer, $other, $d)
        }
    }
    Report '3p:everyone-sees-everyone' ($worst -lt 0.6) ($seen -join ', ')
    $ma = Money A; $mb = Money B; $mc = Money C
    Report '3p:money-equal' (($ma -eq $mb) -and ($mb -eq $mc)) "A=$ma B=$mb C=$mc"
    Get-Shot A '3p_A' | Out-Null; Get-Shot C '3p_C' | Out-Null
}

# Teleport and verify (a tp right after a session reset occasionally doesn't stick).
function Tp($pc, [double]$x, [double]$y, [double]$z) {
    for ($i = 0; $i -lt 4; $i++) {
        Do-Cmd $pc ("tp {0} {1} {2}" -f $x, $y, $z) 5 | Out-Null
        Start-Sleep -Milliseconds 600
        $p = (Get-State $pc).player.pos
        if ($p -and [Math]::Sqrt([Math]::Pow($p[0] - $x, 2) + [Math]::Pow($p[2] - $z, 2)) -lt 4) { return $true }
    }
    $false
}

function Revive-State($pc) { ((Do-Cmd $pc 'revive state' 5) -split '\| ok \| ')[1] -replace ' \| t=.*', '' }

function Test-Revive {
    Do-Cmd A 'goto Overworld Redux' 50 | Out-Null; Do-Cmd B 'goto Overworld Redux' 50 | Out-Null
    Do-Cmd A 'waitplayer 40' 45 | Out-Null; Do-Cmd B 'waitplayer 40' 45 | Out-Null
    Do-Cmd A 'god on' 5 | Out-Null; Do-Cmd B 'god off' 5 | Out-Null
    Tp A 84 28 -133 | Out-Null                           # A nearby (alive teammate), out of the slimes' way
    Tp B 90 28 -140 | Out-Null                           # B among the slimes
    $down = $false
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep 2; if ((Revive-State B) -match 'downed=True') { $down = $true; break } }
    $sb = Get-State B
    Report 'revive:B-downed-not-dead' ($down -and $sb.scene -eq 'Overworld Redux' -and $sb.player.hp -ge 1) "B $(Revive-State B) scene=$($sb.scene) hp=$($sb.player.hp)"
    Start-Sleep 3
    $sb = Get-State B
    Report 'revive:stays-down-no-death' ($sb.scene -eq 'Overworld Redux' -and (Revive-State B) -match 'downed=True') "after 3 s: scene=$($sb.scene) $(Revive-State B)"
    $aView = Revive-State A
    $bId = (Get-State B).net.localId
    Report 'revive:A-knows' ($aView -match "downedPlayers=\[[^\]]*$bId") "A: $aView"
    $bv = Revive-State B
    Report 'revive:B-spectates-A' ($bv -match 'spectating=0' -and $bv -notmatch 'camShift=\(0\.00, 0\.00, 0\.00\)') "B: $bv"
    Get-Shot B 'revive_spectate_B' | Out-Null
    Get-Shot A 'revive_downed_seen_by_A' | Out-Null
    # A walks over and stands next to B
    $bp = (Get-State B).player.pos
    Tp A ($bp[0] + 3) $bp[1] $bp[2] | Out-Null
    Do-Cmd A ("moveto {0} {1} 4 0.8" -f $bp[0], $bp[2]) 8 | Out-Null    # walk up to B like a player would
    Start-Sleep 1
    Get-Shot A 'revive_progress_A' | Out-Null
    Start-Sleep 4
    $rs = Revive-State B; $sb = Get-State B
    $revLog = @(Get-Log B -Pattern 'revived you' -Last 50).Count
    # B's log is the ground truth (slimes may already have downed B again by the time we look at state)
    Report 'revive:revived' ($revLog -ge 1) "B log 'revived you' x$revLog; now: $rs hp=$($sb.player.hp)/$($sb.player.maxhp)"
    # give-up path: go down again, then respawn voluntarily
    Tp A 60 28 -125 | Out-Null                            # A away so B isn't instantly revived
    $down2 = $false
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep 2; if ((Revive-State B) -match 'downed=True') { $down2 = $true; break } }
    Do-Cmd B 'revive giveup' 5 | Out-Null
    for ($j = 0; $j -lt 20; $j++) { if ((Get-State B).scene -eq 'Loading') { break }; Start-Sleep 1 }
    $r = Do-Cmd B 'waitplayer 60' 70
    $sb = Get-State B
    # (a save's first death plays the 'Resurrection' walk-through scene; that counts as the respawn path)
    Report 'revive:giveup-respawns' ($down2 -and ($sb.scene -eq 'Resurrection' -or $sb.player.hp -gt 0) -and (Revive-State B) -match 'downed=False') "downed again=$down2; after give-up scene=$($sb.scene) hp=$($sb.player.hp) ($r)"
    Do-Cmd B 'god on' 5 | Out-Null
}

function Test-Coins {
    # 1. owner picks own coins (B parked ~10 m away)
    Test-CoinScenario 'owner-picks' 'A' 7 $Beach @(18, 0.6, -232) 1.5
    # 2. B drops coins next to A (B far): A picks B's coins via claim
    Test-CoinScenario 'claim' 'B' 6 @(8, 0.6, -238) @(8, 0.6, -232) 6
    # 3. both on the same spot, race
    Test-CoinScenario 'race' 'B' 8 $Beach $Beach 1.5
}
