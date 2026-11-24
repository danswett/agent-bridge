#Requires -Version 7.4
<#
.SYNOPSIS
  The product-os pin (#4104, #4306): reads quality/plan.ps1 with the PowerShell parser and writes one line of JSON,
  { "problems": [...], "runners": [...] }; RunnerRouting.ProductOsProblems holds runners to ProductRunners.
  An allow-list: $runners is one [ordered] map of quoted constants; $productOs is written only as
  @($runners.Keys | Where-Object { <inert filter> } | ForEach-Object { $runners[$_] }); $outputs keys are constants,
  or $job inside 'foreach ($job in $Jobs)' before the one product-os statement; $lines and the output file appear only
  in the pinned output statements. Writing a variable or file by name (*-Variable, Invoke-Expression, [ref],
  -OutVariable, splatting into a script command, dot-sourcing, redirection, a computed command or key) is refused,
  and so is every use of these variables the pin doesn't know. So is a function plan.ps1 doesn't own (its nine, each once,
  ConvertTo-JsonArray as written), a filter, an alias, a module or a type. Its limit: it reads plan.ps1 alone, so another
  process plan.ps1 starts (& ./x.ps1, pwsh -c, Start-Process) could write the output file; a write by name to it is refused.
#>using namespace System.Management.Automation.Language
param([Parameter(Mandatory)] [string] $Plan)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$errors = $null
$ast = [Parser]::ParseFile((Resolve-Path -LiteralPath $Plan).Path, [ref]$null, [ref]$errors)
$problems = [Collections.Generic.List[string]]::new()
function Add-Problem([Ast]$At, [string]$Why) { $problems.Add("plan.ps1:$($At.Extent.StartLineNumber) $Why`: $(($At.Extent.Text -replace '\s+', ' ').Substring(0, [Math]::Min(100, ($At.Extent.Text -replace '\s+', ' ').Length)))") }
if ($errors) { $problems.Add("plan.ps1 doesn't parse: $($errors[0].Message)") }
function Find([scriptblock]$Predicate) { @($ast.FindAll($Predicate, $true)) }
function Norm([Ast]$Node) { ($Node.Extent.Text -replace '\s+', ' ').Trim() }
# A variable's name without its scope (script:, global:, ...), lower-cased: PowerShell names are case-insensitive.
function Name([VariableExpressionAst]$V) { ($V.VariablePath.UserPath -replace '^(?i)(script|global|local|private|using):', '').ToLowerInvariant() }
function Is-Var($Node, [string]$Name) { $Node -is [VariableExpressionAst] -and (Name $Node) -eq $Name.ToLowerInvariant() }
function Inside([Ast]$Node, [Ast]$Outer) { for ($p = $Node; $p; $p = $p.Parent) { if ($p -eq $Outer) { return $true } }; $false }
# The two pinned output statements, as the plan writes them.
$pinnedText = @(
    'if ($env:GITHUB_OUTPUT) { $lines | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8 } else { $lines | Write-Output }',
    'if ($env:GITHUB_STEP_SUMMARY) { $md | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8 } else { $md | Write-Output }',
    '$lines = $outputs.GetEnumerator() | ForEach-Object { $line = "$($_.Key)=$($_.Value)"; if ($line -match ''[\r\n]'') { throw "plan output $($_.Key) holds a line break, which would write a second output line" }; $line }')
$pinned = @(Find { param($n) $n -is [StatementAst] -and (Norm $n) -in $pinnedText })
foreach ($text in $pinnedText) { if (@($pinned | Where-Object { (Norm $_) -eq $text }).Count -ne 1) { $problems.Add("plan.ps1 doesn't have exactly one: $text") } }
function In-Pinned([Ast]$Node) { foreach ($p in $pinned) { if (Inside $Node $p) { return $true } }; $false }
# --- Anything that can write a variable by name, or reach the output file another way. ---
$byName = '^(?i)(Set|New|Clear|Remove|Get)-Variable$|^(?i)(Set-Alias|New-Alias|sal|nal|Import-Module|ipmo|Add-Type|Update-TypeData|Update-FormatData|Start-Process|saps|start|Export-Clixml|sv|nv|clv|rv|gv|Invoke-Expression|iex|Invoke-Command|icm|Set-Item|si|New-Item|ni|Out-File|Set-Content|sc|Tee-Object|tee|Copy-Item|Move-Item)$'
foreach ($c in Find { param($n) $n -is [CommandAst] }) {
    $name = $c.GetCommandName()
    if (-not $name) { Add-Problem $c 'a command whose name is computed'; continue }
    if ($name -match $byName) { Add-Problem $c "$name can write a variable or a file by name" }
    if ($name -match '^(?i)(Add-Content|ac)$' -and -not (In-Pinned $c)) { Add-Problem $c 'a file write outside the two pinned output statements' }
    if ($c.InvocationOperator -eq 'Dot' -or ($c.InvocationOperator -eq 'Ampersand' -and $c.CommandElements[0] -isnot [StringConstantExpressionAst])) { Add-Problem $c 'a dot-sourced or computed call' }
    foreach ($e in $c.CommandElements) {
        if ($e -is [CommandParameterAst] -and $e.ParameterName -match '^(?i)(OutVariable|ov|PipelineVariable|pv|ErrorVariable|ev|WarningVariable|wv|InformationVariable|iv)$') { Add-Problem $e "-$($e.ParameterName) writes a variable by name" }
        if ($e -is [VariableExpressionAst] -and $e.Splatted -and $name -notmatch '^(dotnet|git)$') { Add-Problem $e "splatting into $name, which could carry any parameter" }
    }
}
foreach ($r in Find { param($n) $n -is [FileRedirectionAst] }) { Add-Problem $r 'a file redirection' }
# Only plan.ps1's own functions, each defined once; ConvertTo-JsonArray, which builds the output, exactly as written.
$owned = 'Select-Suites', 'ConvertTo-JsonArray', 'Test-WholeRing', 'Test-NotOwn', 'Test-OwnReason', 'Cell', 'Text', 'Code', 'Files'
$defined = @(Find { param($n) $n -is [FunctionDefinitionAst] })
foreach ($d in $defined) { if ($d.IsFilter -or $d.IsWorkflow -or $d.Name -notin $owned) { Add-Problem $d "a function plan.ps1 doesn't own, which could replace a command" } }
foreach ($o in $owned) { if (@($defined | Where-Object Name -eq $o).Count -ne 1) { $problems.Add("plan.ps1 must define $o exactly once") } }
if (@($defined | Where-Object { $_.Name -eq 'ConvertTo-JsonArray' -and (Norm $_) -ne 'function ConvertTo-JsonArray($items) { ConvertTo-Json -InputObject @($items) -Compress }' }).Count) { $problems.Add('plan.ps1 changes ConvertTo-JsonArray, which builds the product-os output') }
if ($ast.UsingStatements.Count -or $ast.ScriptRequirements.RequiredModules.Count) { $problems.Add('plan.ps1 imports a module (using or #Requires -Modules)') }
foreach ($t in Find { param($n) $n -is [TypeExpressionAst] -or $n -is [ConvertExpressionAst] -or $n -is [TypeConstraintAst] }) {
    $type = if ($t -is [ConvertExpressionAst]) { $t.Type.TypeName.FullName } else { $t.TypeName.FullName }
    if ($type -match '^(?i)(ref|System\.Management\.Automation\.PSReference|(System\.)?Environment|(System\.)?IO\.(File|FileInfo|StreamWriter|FileStream|Directory)|scriptblock|System\.Management\.Automation\.ScriptBlock|psvariable)$') { Add-Problem $t "[$type]" }
}
foreach ($v in Find { param($n) $n -is [VariableExpressionAst] }) {
    if ((Name $v) -in 'executioncontext', 'pscmdlet') { Add-Problem $v "`$$((Name $v)) can reach any variable" }
    if ($v.VariablePath.UserPath -match '^(?i)(variable|function):') { Add-Problem $v 'a variable: or function: drive path' }
    if ($v.VariablePath.UserPath -match '^(?i)env:GITHUB_OUTPUT$' -and -not (In-Pinned $v)) { Add-Problem $v '$env:GITHUB_OUTPUT outside the pinned output statement' }
}
foreach ($s in Find { param($n) $n -is [StringConstantExpressionAst] -or $n -is [ExpandableStringExpressionAst] }) {
    if ($s.Value -match '(?i)GITHUB_OUTPUT|set-output|save-state|variable:') { Add-Problem $s 'a string naming the output file, a workflow command or a variable drive' }
}
foreach ($m in Find { param($n) $n -is [InvokeMemberExpressionAst] }) {
    if ($m.Member -isnot [StringConstantExpressionAst]) { Add-Problem $m 'a method whose name is computed'; continue }
    if ($m.Member.Value -match '^(?i)(Invoke|InvokeReturnAsIs|SetValue|set_Item|Create|InvokeScript|NewScriptBlock)$') { Add-Problem $m ".$($m.Member.Value)() can run or write anything" }
}
# --- $runners: one constant [ordered] map. ---
$runnerValues = @()
$assignments = Find { param($n) $n -is [AssignmentStatementAst] }
function Target([AssignmentStatementAst]$A) {
    $left = $A.Left
    while ($left -is [ConvertExpressionAst] -or $left -is [AttributedExpressionAst]) { $left = $left.Child }
    $left
}
# The variable an assignment target writes: $x, $x[..], $x.y, [type]$x.
function Root($Left) {
    while ($Left -is [IndexExpressionAst] -or $Left -is [MemberExpressionAst] -or $Left -is [ConvertExpressionAst] -or $Left -is [AttributedExpressionAst]) {
        $Left = if ($Left -is [IndexExpressionAst] -or $Left -is [MemberExpressionAst]) { $Left.Target } else { $Left.Child }
    }
    $Left
}
$watched = 'runners', 'productos', 'outputs', 'lines'
$allowedWrites = [Collections.Generic.HashSet[Ast]]::new()
$productOsStatement = $null
foreach ($a in $assignments) {
    $target = Target $a
    $root = Root $target
    if ($root -isnot [VariableExpressionAst] -or (Name $root) -notin $watched) { continue }
    $var = Name $root
    if ($a.Operator -ne 'Equals') { Add-Problem $a "a $($a.Operator) assignment to `$$var"; continue }
    switch ($var) {
        'runners' {
            $map = $a.Right.Expression
            $pairs = if ($target -is [VariableExpressionAst] -and $map -is [ConvertExpressionAst] -and $map.Type.TypeName.Name -eq 'ordered' -and $map.Child -is [HashtableAst]) { $map.Child.KeyValuePairs } else { $null }
            $constant = $pairs -and @($pairs | Where-Object { -not ($_.Item1 -is [StringConstantExpressionAst] -and $_.Item2 -is [PipelineAst] -and
                        $_.Item2.PipelineElements.Count -eq 1 -and $_.Item2.PipelineElements[0].Expression -is [StringConstantExpressionAst] -and
                        $_.Item2.PipelineElements[0].Expression.StringConstantType -eq 'SingleQuoted') }).Count -eq 0
            if (-not $constant -or $runnerValues) { Add-Problem $a '$runners must be assigned once, as [ordered]@{ quoted key = quoted value; ... }'; continue }
            $runnerValues = @($pairs | ForEach-Object { $_.Item2.PipelineElements[0].Expression.Value })
            [void]$allowedWrites.Add($a)
        }
        'productos' {
            # @($runners.Keys | Where-Object { <filter> } | ForEach-Object { $runners[$_] })
            $array = $a.Right.Expression
            $pipe = if ($target -is [VariableExpressionAst] -and $array -is [ArrayExpressionAst] -and $array.SubExpression.Statements.Count -eq 1) { $array.SubExpression.Statements[0] } else { $null }
            $ok = $pipe -is [PipelineAst] -and $pipe.PipelineElements.Count -eq 3
            if ($ok) {
                $first, $filter, $map = $pipe.PipelineElements
                $ok = $first -is [CommandExpressionAst] -and $first.Expression -is [MemberExpressionAst] -and $first.Expression -isnot [InvokeMemberExpressionAst] -and
                    (Is-Var $first.Expression.Expression 'runners') -and $first.Expression.Member.Value -eq 'Keys' -and
                    $filter -is [CommandAst] -and $filter.GetCommandName() -eq 'Where-Object' -and $filter.CommandElements.Count -eq 2 -and $filter.CommandElements[1] -is [ScriptBlockExpressionAst] -and
                    $map -is [CommandAst] -and $map.GetCommandName() -eq 'ForEach-Object' -and $map.CommandElements.Count -eq 2 -and (Norm $map.CommandElements[1]) -eq '{ $runners[$_] }'
                if ($ok) {
                    $body = $filter.CommandElements[1]
                    $ok = @($body.FindAll({ param($n) $n -is [AssignmentStatementAst] -or $n -is [CommandAst] -or $n -is [InvokeMemberExpressionAst] -or $n -is [SubExpressionAst] -or
                                ($n -is [VariableExpressionAst] -and (Name $n) -in $watched) }, $true)).Count -eq 0
                }
            }
            if (-not $ok) { Add-Problem $a '$productOs is written as something other than @($runners.Keys | Where-Object { ... } | ForEach-Object { $runners[$_] })'; continue }
            [void]$allowedWrites.Add($a)
        }
        'outputs' {
            if ($target -is [VariableExpressionAst]) {
                if ((Norm $a) -ne '$outputs = [ordered]@{}' -or @($allowedWrites | Where-Object { (Norm $_) -eq '$outputs = [ordered]@{}' }).Count -gt 0) { Add-Problem $a '$outputs must be assigned once, as [ordered]@{}' } else { [void]$allowedWrites.Add($a) }
                continue
            }
            if ($target -isnot [IndexExpressionAst] -or -not (Is-Var $target.Target 'outputs')) { Add-Problem $a 'a write into $outputs other than $outputs[<key>] ='; continue }
            $key = $target.Index
            if ($key -is [StringConstantExpressionAst] -and $key.StringConstantType -eq 'SingleQuoted' -and $key.Value -ceq 'product-os') {
                if ((Norm $a) -ne "`$outputs['product-os'] = ConvertTo-JsonArray `$productOs" -or $productOsStatement) { Add-Problem $a "`$outputs['product-os'] must be written once, as ConvertTo-JsonArray `$productOs"; continue }
                $productOsStatement = $a
            } elseif ($key -is [StringConstantExpressionAst] -and $key.Value -notmatch '(?i)product-?os') {
            } elseif (Is-Var $key 'job') {
                $loop = $a.Parent
                while ($loop -and $loop -isnot [ForEachStatementAst]) { $loop = $loop.Parent }
                if (-not $loop -or -not (Is-Var $loop.Variable 'job') -or -not (Is-Var $loop.Condition.PipelineElements[0].Expression 'Jobs')) { Add-Problem $a '$outputs[$job] outside foreach ($job in $Jobs)'; continue }
                $a | Add-Member -NotePropertyName ByJob -NotePropertyValue $true -Force
            } else { Add-Problem $a 'a computed or product-os-like key into $outputs'; continue }
            [void]$allowedWrites.Add($a)
        }
        'lines' { if (-not (In-Pinned $a)) { Add-Problem $a '$lines written outside the pinned statement' } else { [void]$allowedWrites.Add($a) } }
    }
}
if (-not $productOsStatement) { $problems.Add("plan.ps1 never writes `$outputs['product-os'] = ConvertTo-JsonArray `$productOs") }
elseif (@($allowedWrites | Where-Object { $_.PSObject.Properties['ByJob'] -and $_.Extent.StartOffset -gt $productOsStatement.Extent.StartOffset }).Count -gt 0) {
    $problems.Add("plan.ps1 writes `$outputs[`$job] after the product-os statement, which a job named product-os would overwrite")
}
if (-not $runnerValues) { $problems.Add('plan.ps1 has no $runners map') }
# --- Every other use of the watched variables is a known read. ---
foreach ($v in Find { param($n) $n -is [VariableExpressionAst] -and (Name $n) -in $watched }) {
    $p = $v.Parent
    $inAllowedWrite = $false; foreach ($w in $allowedWrites) { if (Inside $v $w) { $inAllowedWrite = $true; break } }
    $known = switch (Name $v) {
        'productos' { ($p -is [MemberExpressionAst] -and $p -isnot [InvokeMemberExpressionAst] -and $p.Member.Value -eq 'Count' -and $p.Expression -eq $v) -or
            ($inAllowedWrite) -or ($p -is [CommandExpressionAst] -and $p.Parent -is [PipelineAst] -and $p.Parent.PipelineElements.Count -eq 1 -and $p.Parent.Parent.Parent -is [ArrayExpressionAst] -and $p.Parent.Parent.Parent.Parent -is [BinaryExpressionAst] -and $p.Parent.Parent.Parent.Parent.Operator -eq 'Join') -or
            ($p -is [CommandAst] -and $p.GetCommandName() -eq 'ConvertTo-JsonArray' -and $productOsStatement -and (Inside $v $productOsStatement)) }
        'runners' { $inAllowedWrite -and ($p -is [MemberExpressionAst] -or $p -is [IndexExpressionAst] -or $p -is [AssignmentStatementAst]) }
        'outputs' { $inAllowedWrite -or ($p -is [IndexExpressionAst] -and $p.Target -eq $v -and -not ($p.Parent -is [AssignmentStatementAst] -and $p.Parent.Left -eq $p)) -or
            ($p -is [InvokeMemberExpressionAst] -and $p.Member.Value -eq 'GetEnumerator' -and $p.Expression -eq $v) }
        'lines' { In-Pinned $v }
    }
    if (-not $known) { Add-Problem $v "`$$((Name $v)) used in a shape the pin doesn't know" }
}
foreach ($s in Find { param($n) $n -is [StringConstantExpressionAst] -and $n.Value -match '(?i)product-?os' }) {
    if (-not ($productOsStatement -and (Inside $s $productOsStatement))) { Add-Problem $s 'product-os named outside its one output statement' }
}
# A double-quoted string's own text (its $(...) parts are checked as code above) must not name product-os.
foreach ($s in Find { param($n) $n -is [ExpandableStringExpressionAst] }) {
    $text = $s.Extent.Text
    foreach ($nested in @($s.NestedExpressions | Sort-Object { $_.Extent.StartOffset } -Descending)) { $text = $text.Remove($nested.Extent.StartOffset - $s.Extent.StartOffset, $nested.Extent.EndOffset - $nested.Extent.StartOffset) }
    if ($text -match '(?i)product-os') { Add-Problem $s 'product-os in a double-quoted string' }
}
[pscustomobject]@{ problems = @($problems | Sort-Object -Unique); runners = $runnerValues } | ConvertTo-Json -Compress
