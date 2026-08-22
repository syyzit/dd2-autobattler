param(
    [string]$LogPath,
    [switch]$Today
)

$ErrorActionPreference = "Stop"
function Get-GameDir {
    if ($env:DD2_GAME_DIR -and (Test-Path $env:DD2_GAME_DIR)) { return $env:DD2_GAME_DIR }
    $roots = @(
        "D:\SteamLibrary\steamapps\common",
        "C:\Program Files (x86)\Steam\steamapps\common",
        "C:\Program Files\Steam\steamapps\common"
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "Darkest*" } |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "Could not find the Darkest Dungeon II folder. Set DD2_GAME_DIR."
}
$gameDir = Get-GameDir
$root = Join-Path $gameDir "BepInEx\Dd2Autobattler\logs"

function Get-Logs {
    if ($LogPath) { return @(Get-Item $LogPath) }
    $dirs = Get-ChildItem $root -Directory | Sort-Object LastWriteTime -Descending
    if ($Today) {
        $day = (Get-Date).Date
        return @(
            $dirs | Where-Object { $_.LastWriteTime.Date -eq $day } |
            ForEach-Object { Get-Item (Join-Path $_.FullName "decisions.jsonl") } |
            Where-Object { Test-Path $_.FullName } |
            Sort-Object LastWriteTime
        )
    }
    $dir = $dirs | Select-Object -First 1
    return @(Get-Item (Join-Path $dir.FullName "decisions.jsonl"))
}

function Add-Count([hashtable]$map, $key) {
    if (-not $key) { $key = "?" }
    $k = [string]$key
    if (-not $map.ContainsKey($k)) { $map[$k] = 0 }
    $map[$k]++
}

$files = Get-Logs
if ($files.Count -eq 0) { Write-Error "No decision logs found." }

$fights = New-Object System.Collections.Generic.List[object]
$reasons = @{}
$items = @{}
$notes = New-Object System.Collections.Generic.List[string]
$close = 0
$legalTurns = 0
$hitAddWithController = 0
$healSkippedDd = 0
$healSkippedLow = 0
$comboApplyNoSpender = 0
$punchedCombo = 0
$savedCombo = 0
$itemWaste = 0
$itemGood = 0
$focusBoss = 0
$passTurns = 0
$moveTurns = 0
$supportTurns = 0
$errorCount = 0
$errorNotes = New-Object System.Collections.Generic.List[string]
$gapBuckets = [ordered]@{ "0-4" = 0; "4-10" = 0; "10-25" = 0; "25+" = 0 }
$shadowAgree = 0
$shadowDisagree = 0
$shadowNotes = New-Object System.Collections.Generic.List[string]
$fight = $null

Write-Host "LOGS $($files.Count)"
foreach ($file in $files) { Write-Host "  $($file.FullName)" }

foreach ($file in $files) {
    foreach ($line in Get-Content $file.FullName) {
        $o = $null
        try { $o = $line | ConvertFrom-Json } catch { continue }
        switch ($o.type) {
            "fight_start" {
                $fight = [pscustomobject]@{
                    Id = $o.fight
                    File = $file.Name
                    Turns = 0
                    Complete = $null
                    Retreat = $null
                    Boss = $null
                    Reasons = @{}
                    StartHeroes = $null
                    LastHeroes = $null
                    Focus = $null
                    Items = 0
                    Heals = 0
                    AddHits = 0
                    ControllerHits = 0
                }
                $fights.Add($fight) | Out-Null
            }
            "shadow_result" {
                $match = [bool]$o.match
                if ($match) { $shadowAgree++ } else { $shadowDisagree++ }
                $botSkill = "?"
                $humanSkill = "?"
                $botReason = ""
                $rank = -1
                $gap = 0
                if ($o.bot) { $botSkill = [string]$o.bot.skill; $botReason = [string]$o.bot.reason }
                if ($o.human) { $humanSkill = [string]$o.human.skill; $rank = [int]$o.human.rank }
                if ($o.gap -ne $null) { $gap = [double]$o.gap }
                if (-not $match) {
                    $shadowNotes.Add(("{0} t{1} you={2} bot={3} ({4}) rank={5} gap={6:0.0}" -f $o.fight, $o.turn_index, $humanSkill, $botSkill, $botReason, $rank, $gap))
                }
            }
            "error" {
                $errorCount++
                $msg = [string]$o.message
                $ex = [string]$o.exception
                $errorNotes.Add(("{0} {1}: {2}" -f $o.fight, $ex, $msg))
            }
            "fight_end" {
                if ($fight) {
                    $fight.Complete = $o.complete
                    $fight.Retreat = $o.retreat
                    $fight.Boss = $o.boss
                    if ($o.turns) { $fight.Turns = $o.turns }
                }
            }
            "turn" {
                if (-not $fight) { continue }
                $fight.Turns++
                $r = [string]$o.reason
                Add-Count $reasons $r
                Add-Count $fight.Reasons $r
                if ($r -like "item_*") {
                    Add-Count $items $r
                    $fight.Items++
                }
                if ($r -like "heal*") { $fight.Heals++ }
                if ($r -eq "pass") { $passTurns++ }
                if ($r -eq "move_last_resort") { $moveTurns++ }
                if ($r -eq "support") { $supportTurns++ }
                if ($r -like "focus_boss*" -or $r -like "focus_summoner*" -or $r -like "focus_rezzer*") { $focusBoss++ }
                if ($r -eq "save_combo") { $savedCombo++ }

                if (-not $fight.StartHeroes -and $o.heroes) { $fight.StartHeroes = $o.heroes }
                if ($o.heroes) { $fight.LastHeroes = $o.heroes }
                if ($o.focus) { $fight.Focus = $o.focus }

                $chosen = $o.chosen
                $legal = @($o.legal)
                if ($legal.Count -gt 0) {
                    $legalTurns++
                    $sorted = $legal | Sort-Object { [double]$_.score } -Descending
                    if ($sorted.Count -ge 2) {
                        $gap = [double]$sorted[0].score - [double]$sorted[1].score
                        if ($gap -ge 0 -and $gap -lt 4) { $close++ }
                        if ($gap -lt 4) { $gapBuckets["0-4"]++ }
                        elseif ($gap -lt 10) { $gapBuckets["4-10"]++ }
                        elseif ($gap -lt 25) { $gapBuckets["10-25"]++ }
                        else { $gapBuckets["25+"]++ }
                    }

                    $controllerLegal = $false
                    $chosenIsAdd = $false
                    foreach ($row in $legal) {
                        $why = [string]$row.focus_why
                        if ($row.enemy -and $why -and $why -notlike "*add*" -and (
                            $why -like "*boss*" -or $why -like "*summon*" -or $why -like "*rez*" -or $why -like "*support*")) {
                            $controllerLegal = $true
                        }
                    }
                    if ($chosen -and $legal.Count -gt 0) {
                        $picked = $legal | Where-Object { $_.skill -eq $chosen.skill -and $_.target -eq $chosen.target } | Select-Object -First 1
                        if ($picked -and [string]$picked.focus_why -like "*add*") { $chosenIsAdd = $true }
                        if ($controllerLegal -and $chosenIsAdd) {
                            $hitAddWithController++
                            $fight.AddHits++
                            $notes.Add(("{0} t{1} HIT ADD while controller legal: {2} -> {3} ({4})" -f $fight.Id, $fight.Turns, $chosen.skill, $chosen.target, $r))
                        }
                        if ($picked -and $picked.enemy -and [string]$picked.focus_why -notlike "*add*" -and [string]$picked.focus_why -ne "trash") {
                            $fight.ControllerHits++
                        }

                        if ($picked -and $picked.kind -eq "Attack" -and $picked.combo -eq $true -and -not $picked.kills) {
                            $cons = @($picked.consume)
                            if ($cons -notcontains "combo") { $punchedCombo++ }
                        }
                        if ($picked -and ($picked.apply -contains "combo") -and $o.synergy) {
                            $spenders = @($o.synergy.combo_spenders)
                            if ($spenders.Count -eq 0) { $comboApplyNoSpender++ }
                        }
                    }

                    $ddAlly = $false
                    $lowAlly = $false
                    foreach ($row in $legal) {
                        if ($row.kind -eq "Heal" -and -not $row.enemy) {
                            if ($row.deaths_door) { $ddAlly = $true }
                            if ([double]$row.target_hp -gt 0 -and $o.heroes) {
                                foreach ($h in @($o.heroes)) {
                                    if ($h.guid -eq $row.target -and [double]$h.hp_pct -le 0.35) { $lowAlly = $true }
                                }
                            }
                        }
                    }
                    $choseHeal = $r -like "heal*"
                    if ($ddAlly -and -not $choseHeal) { $healSkippedDd++ }
                    if ($lowAlly -and -not $choseHeal -and -not $ddAlly) { $healSkippedLow++ }
                }

                if ($chosen -and $r -like "item_*") {
                    $tgtHero = $null
                    if ($o.heroes) { $tgtHero = @($o.heroes) | Where-Object { $_.guid -eq $chosen.target } | Select-Object -First 1 }
                    if ($r -eq "item_heal" -and $tgtHero -and [double]$tgtHero.hp_pct -gt 0.55 -and -not $tgtHero.deaths_door) {
                        $itemWaste++
                    } else {
                        $itemGood++
                    }
                }
            }
        }
    }
}

$wins = @($fights | Where-Object { $_.Complete -eq $true }).Count
$lost = @($fights | Where-Object { $_.Complete -eq $false }).Count
$retreats = @($fights | Where-Object { $_.Retreat -eq $true }).Count

Write-Host ""
Write-Host "======== RUN ========"
Write-Host ("fights={0}  won={1}  lost/abort={2}  retreat={3}  turns_with_legal={4}" -f $fights.Count, $wins, $lost, $retreats, $legalTurns)

Write-Host ""
Write-Host "======== FIGHTS ========"
foreach ($f in $fights) {
    $flag = "win"
    if ($f.Complete -eq $false) { $flag = "LOST" }
    elseif ($f.Retreat) { $flag = "RETREAT" }
    $top = ($f.Reasons.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 3 |
        ForEach-Object { "{0}:{1}" -f $_.Key, $_.Value }) -join ", "
    $party = ""
    if ($f.LastHeroes) {
        $party = ((@($f.LastHeroes) | ForEach-Object {
            $mark = ""
            if ($_.deaths_door) { $mark = " DD" }
            if (-not $_.living -or $_.corpse) { $mark = " DEAD" }
            "{0} {1}pct{2}" -f $_.name, [int](100 * [double]$_.hp_pct), $mark
        }) -join " / ")
    }
    Write-Host ("  {0,-7} {1,-42} t={2,-3} items={3} heals={4} ctrl={5} add={6}" -f $flag, $f.Id, $f.Turns, $f.Items, $f.Heals, $f.ControllerHits, $f.AddHits)
    Write-Host ("          {0}" -f $top)
    if ($party) { Write-Host ("          end: {0}" -f $party) }
}

Write-Host ""
Write-Host "======== WHY IT CLICKED ========"
$reasons.GetEnumerator() | Sort-Object Value -Descending |
    ForEach-Object { Write-Host ("  {0,5}  {1}" -f $_.Value, $_.Key) }

if ($items.Count -gt 0) {
    Write-Host ""
    Write-Host "======== ITEMS ========"
    $items.GetEnumerator() | Sort-Object Value -Descending |
        ForEach-Object { Write-Host ("  {0,5}  {1}" -f $_.Value, $_.Key) }
    Write-Host ("  good_or_ok={0}  snack_waste={1}" -f $itemGood, $itemWaste)
}

Write-Host ""
Write-Host "======== OPPORTUNITIES ========"
Write-Host ("  silent_fallbacks (jsonl error): {0}" -f $errorCount)
Write-Host ("  close_scores (<4 gap):          {0}" -f $close)
Write-Host "  score_margin histogram:"
foreach ($k in $gapBuckets.Keys) {
    Write-Host ("    {0,-6} {1}" -f $k, $gapBuckets[$k])
}
Write-Host ("  hit ADD while controller legal: {0}" -f $hitAddWithController)
Write-Host ("  skipped heal on Deaths Door:    {0}" -f $healSkippedDd)
Write-Host ("  skipped heal on ally <=35pct:   {0}" -f $healSkippedLow)
Write-Host ("  applied Combo with no spender:  {0}" -f $comboApplyNoSpender)
Write-Host ("  punched Combo without spending: {0}" -f $punchedCombo)
Write-Host ("  save_combo reasons:             {0}" -f $savedCombo)
Write-Host ("  focus_boss/summoner/rezzer:     {0}" -f $focusBoss)
Write-Host ("  pass / support / move:          {0} / {1} / {2}" -f $passTurns, $supportTurns, $moveTurns)
$shadowTotal = $shadowAgree + $shadowDisagree
if ($shadowTotal -gt 0) {
    Write-Host ("  shadow agree / disagree:        {0} / {1}" -f $shadowAgree, $shadowDisagree)
}

if ($shadowNotes.Count -gt 0) {
    Write-Host ""
    Write-Host "======== SHADOW DISAGREEMENTS (first 25) ========"
    $shadowNotes | Select-Object -First 25 | ForEach-Object { Write-Host ("  {0}" -f $_) }
}

if ($errorNotes.Count -gt 0) {
    Write-Host ""
    Write-Host "======== SILENT FALLBACKS (first 15) ========"
    $errorNotes | Select-Object -First 15 | ForEach-Object { Write-Host ("  {0}" -f $_) }
}

if ($notes.Count -gt 0) {
    Write-Host ""
    Write-Host "======== ADD-HITS (first 15) ========"
    $notes | Select-Object -First 15 | ForEach-Object { Write-Host ("  {0}" -f $_) }
}

$losses = @($fights | Where-Object { $_.Complete -eq $false })
if ($losses.Count -gt 0) {
    Write-Host ""
    Write-Host "======== LOSSES ========"
    foreach ($f in $losses) {
        Write-Host ("  {0}  t={1}" -f $f.Id, $f.Turns)
        if ($f.StartHeroes) {
            Write-Host "  start party:"
            foreach ($h in @($f.StartHeroes)) {
                Write-Host ("    {0,-14} {1} pct  stress={2}" -f $h.name, [int](100*[double]$h.hp_pct), $h.stress)
            }
        }
        if ($f.LastHeroes) {
            Write-Host "  end party:"
            foreach ($h in @($f.LastHeroes)) {
                $dd = if ($h.deaths_door) { " DD" } else { "" }
                $dead = if (-not $h.living -or $h.corpse) { " DEAD" } else { "" }
                Write-Host ("    {0,-14} {1} pct{2}{3}  stress={4}" -f $h.name, [int](100*[double]$h.hp_pct), $dd, $dead, $h.stress)
            }
        }
        if ($f.Focus -and $f.Focus.enemies) {
            Write-Host "  enemies:"
            foreach ($e in @($f.Focus.enemies)) {
                Write-Host ("    {0,-28} focus={1}  {2}" -f $e.class, $e.focus, $e.why)
            }
        }
    }
}

Write-Host ""
Write-Host "Use -Today to stitch every log from today into one report."
