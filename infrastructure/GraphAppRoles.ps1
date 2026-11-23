<#
.SYNOPSIS
  Grants Microsoft Graph application roles (e.g. User.Read.All) to a service principal,
  such as the web app's system-assigned managed identity. Used by Deploy-Infrastructure.ps1
  when the setting ManagedIdentityGraphAppRoles is not empty.

.DESCRIPTION
  Idempotent: a role that is already assigned is left as it is. The signed-in Azure CLI user needs an
  Entra role that can grant application permissions (Privileged Role Administrator or Global Administrator).
  A new managed identity can take a minute to appear in Microsoft Graph, so the grant is retried.
  Never throws: returns $false and prints a warning with the manual command when a role can't be granted.
#>

function Grant-GraphAppRoles {
    param(
        [Parameter(Mandatory)] [string] $PrincipalId,
        [Parameter(Mandatory)] [string[]] $Roles,
        [int] $Attempts = 6,
        [int] $DelaySeconds = 20
    )

    $graphAppId = '00000003-0000-0000-c000-000000000000'  # Microsoft Graph, same in every tenant
    $ok = $true
    try {
        $graphSp = az ad sp show --id $graphAppId -o json | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $graphSp) { throw "Microsoft Graph service principal not found." }
        $assignmentsUrl = "https://graph.microsoft.com/v1.0/servicePrincipals/$PrincipalId/appRoleAssignments"

        foreach ($roleName in $Roles) {
            $role = $graphSp.appRoles | Where-Object { $_.value -eq $roleName -and $_.allowedMemberTypes -contains 'Application' } | Select-Object -First 1
            if (-not $role) {
                Write-Warning "Microsoft Graph has no application role '$roleName'. Skipped."
                $ok = $false
                continue
            }

            $granted = $false
            for ($i = 1; $i -le $Attempts -and -not $granted; $i++) {
                $existing = az rest --method get --url $assignmentsUrl -o json 2>$null | ConvertFrom-Json
                if ($LASTEXITCODE -eq 0 -and $existing) {
                    if (@($existing.value | Where-Object { $_.resourceId -eq $graphSp.id -and $_.appRoleId -eq $role.id }).Count -gt 0) {
                        Write-Host "  Graph app role $roleName already assigned to $PrincipalId (leave unchanged)"
                        $granted = $true
                        break
                    }
                    $bodyPath = Join-Path $env:TEMP "graph-approle-$PrincipalId-$($role.id).json"
                    (@{ principalId = $PrincipalId; resourceId = $graphSp.id; appRoleId = $role.id } | ConvertTo-Json) |
                        Set-Content -Path $bodyPath -Encoding UTF8
                    az rest --method post --url $assignmentsUrl --headers 'Content-Type=application/json' --body "@$bodyPath" -o none 2>$null
                    $postExit = $LASTEXITCODE
                    Remove-Item $bodyPath -ErrorAction SilentlyContinue
                    if ($postExit -eq 0) {
                        Write-Host "  Graph app role $roleName assigned to $PrincipalId"
                        $granted = $true
                        break
                    }
                }
                if ($i -lt $Attempts) {
                    Write-Host "  Graph app role $roleName not assigned yet (attempt $i of $Attempts), retrying in $DelaySeconds s..."
                    Start-Sleep -Seconds $DelaySeconds
                }
            }
            if (-not $granted) {
                $ok = $false
                Write-Warning ("Could not assign Graph app role $roleName to $PrincipalId. Assign it as Privileged Role Administrator / Global Administrator:`n" +
                    "  az rest --method post --url $assignmentsUrl --headers Content-Type=application/json --body '{""principalId"":""$PrincipalId"",""resourceId"":""$($graphSp.id)"",""appRoleId"":""$($role.id)""}'")
            }
        }
    }
    catch {
        Write-Warning "Graph app role assignment failed: $_"
        $ok = $false
    }
    return $ok
}
