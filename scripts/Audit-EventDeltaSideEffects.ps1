param(
    [Parameter(Mandatory = $true)]
    [string]$SourceOrg,

    [Parameter(Mandatory = $true)]
    [string]$SourceProject,

    [Parameter(Mandatory = $true)]
    [string]$SourcePat,

    [Parameter(Mandatory = $true)]
    [string]$TargetOrg,

    [Parameter(Mandatory = $true)]
    [string]$TargetProject,

    [Parameter(Mandatory = $true)]
    [string]$TargetPat,

    [int]$SourceWorkItemId,

    [int]$TargetWorkItemId,

    [string]$PairsCsv,

    [datetime]$FromUtc = [datetime]::MinValue,

    [datetime]$ToUtc = [datetime]::MaxValue,

    [int]$MatchWindowMinutes = 10,

    [string]$OutputCsv,

    [switch]$IncludeReadOnlyFields
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ObjectModelIgnoredFields = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(
    "System.Rev",
    "System.AreaId",
    "System.IterationId",
    "System.Id",
    "System.Parent",
    "System.RevisedDate",
    "System.AuthorizedAs",
    "System.AttachedFileCount",
    "System.TeamProject",
    "System.NodeName",
    "System.RelatedLinkCount",
    "System.WorkItemType",
    "Microsoft.VSTS.Common.StateChangeDate",
    "System.ExternalLinkCount",
    "System.HyperLinkCount",
    "System.Watermark",
    "System.AuthorizedDate",
    "System.BoardColumn",
    "System.BoardColumnDone",
    "System.BoardLane",
    "SLB.SWT.DateOfClientFeedback",
    "System.CommentCount",
    "System.RemoteLinkCount"
) | ForEach-Object { [void]$ObjectModelIgnoredFields.Add($_) }

$EventDeltaSystemManagedFields = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(
    "System.History",
    "System.CreatedBy",
    "System.CreatedDate",
    "System.ChangedBy",
    "System.ChangedDate",
    "System.PersonId",
    "Microsoft.VSTS.Common.ActivatedDate",
    "Microsoft.VSTS.Common.ActivatedBy",
    "Microsoft.VSTS.Common.ResolvedDate",
    "Microsoft.VSTS.Common.ResolvedBy",
    "Microsoft.VSTS.Common.ClosedDate",
    "Microsoft.VSTS.Common.ClosedBy",
    "System.ClosedDate"
) | ForEach-Object { [void]$EventDeltaSystemManagedFields.Add($_) }

$TargetNoiseFields = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(
    "System.Rev",
    "System.AuthorizedDate",
    "System.RevisedDate",
    "System.ChangedDate",
    "System.ChangedBy",
    "System.AuthorizedAs",
    "System.PersonId",
    "System.Watermark",
    "System.CommentCount",
    "Microsoft.VSTS.Common.StateChangeDate",
    "Microsoft.VSTS.Common.ActivatedDate",
    "Microsoft.VSTS.Common.ActivatedBy",
    "Microsoft.VSTS.Common.ResolvedDate",
    "Microsoft.VSTS.Common.ResolvedBy",
    "Microsoft.VSTS.Common.ClosedDate",
    "Microsoft.VSTS.Common.ClosedBy",
    "System.History",
    "Custom.ReflectedWorkItemId"
) | ForEach-Object { [void]$TargetNoiseFields.Add($_) }

function New-AdoHeader {
    param([Parameter(Mandatory = $true)][string]$Pat)

    $encoded = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$Pat"))
    return @{
        Authorization = "Basic $encoded"
        Accept = "application/json"
    }
}

function Invoke-AdoGet {
    param(
        [Parameter(Mandatory = $true)][string]$Org,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Pat,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $encodedProject = [uri]::EscapeDataString($Project)
    $uri = "https://dev.azure.com/$Org/$encodedProject/$Path"
    return Invoke-RestMethod -Uri $uri -Headers (New-AdoHeader -Pat $Pat) -Method Get
}

function Get-WorkItemUpdates {
    param(
        [Parameter(Mandatory = $true)][string]$Org,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Pat,
        [Parameter(Mandatory = $true)][int]$WorkItemId
    )

    $path = "_apis/wit/workItems/$WorkItemId/updates?api-version=7.1"
    return (Invoke-AdoGet -Org $Org -Project $Project -Pat $Pat -Path $path).value
}

function Get-TargetFieldEditability {
    param(
        [Parameter(Mandatory = $true)][string]$Org,
        [Parameter(Mandatory = $true)][string]$Pat
    )

    $uri = "https://dev.azure.com/$Org/_apis/wit/fields?api-version=7.1"
    $fields = Invoke-RestMethod -Uri $uri -Headers (New-AdoHeader -Pat $Pat) -Method Get
    if ($fields -is [string] -or -not ($fields.PSObject.Properties.Name -contains "value")) {
        throw "Unable to load target field metadata from $uri. Check PAT scopes and organization access."
    }

    $editability = New-Object 'System.Collections.Hashtable' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($field in $fields.value) {
        $referenceName = [string]$field.referenceName
        if ([string]::IsNullOrWhiteSpace($referenceName)) {
            continue
        }
        $readOnly = $false
        if ($field.PSObject.Properties.Name -contains "readOnly") {
            $readOnly = [bool]$field.readOnly
        }
        $editability[$referenceName] = -not $readOnly
    }
    Write-Host "Loaded $($editability.Count) target field metadata entries"
    return $editability
}

function Test-IsEditableTargetField {
    param(
        [Parameter(Mandatory = $true)][string]$FieldName,
        [hashtable]$FieldEditability
    )

    if ($IncludeReadOnlyFields) {
        return $true
    }
    if ($null -eq $FieldEditability -or $FieldEditability.Count -eq 0) {
        return $true
    }
    if (-not $FieldEditability.ContainsKey($FieldName)) {
        return $false
    }
    return [bool]$FieldEditability[$FieldName]
}

function Get-FieldValueText {
    param($Value)

    if ($null -eq $Value) {
        return ""
    }
    if ($Value -is [pscustomobject]) {
        if ($Value.PSObject.Properties.Name -contains "displayName") {
            return [string]$Value.displayName
        }
        if ($Value.PSObject.Properties.Name -contains "name") {
            return [string]$Value.name
        }
    }
    return [string]$Value
}

function Test-HasFields {
    param($Update)

    return $Update.PSObject.Properties.Name -contains "fields" -and $null -ne $Update.fields
}

function Get-FieldChange {
    param(
        $Update,
        [Parameter(Mandatory = $true)][string]$FieldName
    )

    if (-not (Test-HasFields $Update)) {
        return $null
    }
    $property = $Update.fields.PSObject.Properties[$FieldName]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-FieldNewValueText {
    param($FieldChange)

    if ($null -eq $FieldChange) {
        return ""
    }
    if ($FieldChange.PSObject.Properties.Name -contains "newValue") {
        return Get-FieldValueText $FieldChange.newValue
    }
    return ""
}

function ConvertTo-UtcBound {
    param([datetime]$Value)

    if ($Value -eq [datetime]::MinValue -or $Value -eq [datetime]::MaxValue) {
        return $Value
    }
    return $Value.ToUniversalTime()
}

function Get-UpdateChangedDate {
    param($Update)

    if ((Test-HasFields $Update) -and ($Update.fields.PSObject.Properties.Name -contains "System.ChangedDate")) {
        $changedDateField = Get-FieldChange -Update $Update -FieldName "System.ChangedDate"
        if ($changedDateField.PSObject.Properties.Name -contains "newValue") {
            return ([datetime]$changedDateField.newValue).ToUniversalTime()
        }
    }
    if ($Update.revisedDate -and $Update.revisedDate -ne "9999-01-01T00:00:00Z") {
        return ([datetime]$Update.revisedDate).ToUniversalTime()
    }
    return [datetime]::MinValue
}

function Get-FilteredDeltaFields {
    param($Update)

    $fields = New-Object System.Collections.Generic.List[string]
    if (-not (Test-HasFields $Update)) {
        return $fields
    }

    foreach ($property in $Update.fields.PSObject.Properties) {
        if ($ObjectModelIgnoredFields.Contains($property.Name)) {
            continue
        }
        if ($EventDeltaSystemManagedFields.Contains($property.Name)) {
            continue
        }
        [void]$fields.Add($property.Name)
    }
    return $fields
}

function Get-ChangedFieldNames {
    param($Update)

    $names = New-Object System.Collections.Generic.List[string]
    if (-not (Test-HasFields $Update)) {
        return $names
    }
    foreach ($property in $Update.fields.PSObject.Properties) {
        [void]$names.Add($property.Name)
    }
    return $names
}

function Test-UpdateMatchesDelta {
    param(
        $SourceUpdate,
        $TargetUpdate,
        [string[]]$DeltaFields
    )

    if (-not (Test-HasFields $TargetUpdate)) {
        return $false
    }
    foreach ($fieldName in $DeltaFields) {
        if (-not ($TargetUpdate.fields.PSObject.Properties.Name -contains $fieldName)) {
            continue
        }
        $sourceValue = Get-FieldNewValueText (Get-FieldChange -Update $SourceUpdate -FieldName $fieldName)
        $targetValue = Get-FieldNewValueText (Get-FieldChange -Update $TargetUpdate -FieldName $fieldName)
        if ($sourceValue -eq $targetValue) {
            return $true
        }
    }
    return $false
}

function Get-FieldDetails {
    param(
        $Update,
        [string[]]$FieldNames
    )

    $details = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in $FieldNames) {
        $fieldChange = Get-FieldChange -Update $Update -FieldName $fieldName
        $oldValue = ""
        if ($fieldChange.PSObject.Properties.Name -contains "oldValue") {
            $oldValue = Get-FieldValueText $fieldChange.oldValue
        }
        $newValue = Get-FieldNewValueText $fieldChange
        [void]$details.Add("$fieldName='$oldValue'->'$newValue'")
    }
    return ($details -join "; ")
}

function Get-Pairs {
    if ($PairsCsv) {
        return Import-Csv -Path $PairsCsv | ForEach-Object {
            [pscustomobject]@{
                SourceId = [int]$_.SourceId
                TargetId = [int]$_.TargetId
            }
        }
    }

    if ($SourceWorkItemId -le 0 -or $TargetWorkItemId -le 0) {
        throw "Provide either -PairsCsv with SourceId,TargetId columns or both -SourceWorkItemId and -TargetWorkItemId."
    }

    return @([pscustomobject]@{
        SourceId = $SourceWorkItemId
        TargetId = $TargetWorkItemId
    })
}

$results = New-Object System.Collections.Generic.List[object]
$pairs = Get-Pairs
$targetFieldEditability = Get-TargetFieldEditability -Org $TargetOrg -Pat $TargetPat

foreach ($pair in $pairs) {
    Write-Host "Auditing source $($pair.SourceId) -> target $($pair.TargetId)"
    $sourceUpdates = Get-WorkItemUpdates -Org $SourceOrg -Project $SourceProject -Pat $SourcePat -WorkItemId $pair.SourceId
    $targetUpdates = Get-WorkItemUpdates -Org $TargetOrg -Project $TargetProject -Pat $TargetPat -WorkItemId $pair.TargetId

    foreach ($sourceUpdate in $sourceUpdates) {
        $sourceChangedDate = Get-UpdateChangedDate $sourceUpdate
        if ($sourceChangedDate -lt (ConvertTo-UtcBound $FromUtc) -or $sourceChangedDate -gt (ConvertTo-UtcBound $ToUtc)) {
            continue
        }

        [string[]]$deltaFields = @(Get-FilteredDeltaFields $sourceUpdate)
        if ($deltaFields.Count -eq 0) {
            continue
        }

        $windowEnd = $sourceChangedDate.AddMinutes($MatchWindowMinutes)
        $candidate = $targetUpdates |
            Where-Object {
                $targetDate = Get-UpdateChangedDate $_
                $targetDate -ge $sourceChangedDate -and
                $targetDate -le $windowEnd -and
                (Test-UpdateMatchesDelta -SourceUpdate $sourceUpdate -TargetUpdate $_ -DeltaFields $deltaFields)
            } |
            Select-Object -First 1

        if ($null -eq $candidate) {
            [void]$results.Add([pscustomobject]@{
                SourceId = $pair.SourceId
                TargetId = $pair.TargetId
                SourceUpdateId = $sourceUpdate.id
                SourceRev = $sourceUpdate.rev
                SourceChangedDateUtc = $sourceChangedDate.ToString("o")
                DeltaFields = ($deltaFields -join ",")
                TargetUpdateId = ""
                TargetRev = ""
                TargetChangedDateUtc = ""
                UnexpectedFields = "NO_MATCHING_TARGET_UPDATE"
                UnexpectedDetails = ""
            })
            continue
        }

        $unexpectedFields = New-Object System.Collections.Generic.List[string]
        foreach ($fieldName in (Get-ChangedFieldNames $candidate)) {
            if ($TargetNoiseFields.Contains($fieldName)) {
                continue
            }
            if ($deltaFields -contains $fieldName) {
                continue
            }
            if (-not (Test-IsEditableTargetField -FieldName $fieldName -FieldEditability $targetFieldEditability)) {
                continue
            }
            [void]$unexpectedFields.Add($fieldName)
        }

        if ($unexpectedFields.Count -eq 0) {
            continue
        }

        [void]$results.Add([pscustomobject]@{
            SourceId = $pair.SourceId
            TargetId = $pair.TargetId
            SourceUpdateId = $sourceUpdate.id
            SourceRev = $sourceUpdate.rev
            SourceChangedDateUtc = $sourceChangedDate.ToString("o")
            DeltaFields = ($deltaFields -join ",")
            TargetUpdateId = $candidate.id
            TargetRev = $candidate.rev
            TargetChangedDateUtc = (Get-UpdateChangedDate $candidate).ToString("o")
            UnexpectedFields = ($unexpectedFields -join ",")
            UnexpectedDetails = Get-FieldDetails -Update $candidate -FieldNames $unexpectedFields.ToArray()
        })
    }
}

if ($OutputCsv) {
    $results | Export-Csv -Path $OutputCsv -NoTypeInformation -Encoding UTF8
    Write-Host "Wrote $($results.Count) audit rows to $OutputCsv"
} else {
    $results | Format-Table -AutoSize
}
