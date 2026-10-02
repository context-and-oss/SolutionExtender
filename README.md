# SolutionExtender

A **.NET 10 CLI and reusable offline library** for migrating Daxif extended-solution handling to a standard Power Platform CLI (`pac`) workflow. Distributed as a NuGet tool, runnable with **`dnx`**.

PAC handles solution export/unpack/pack/import/publish. SolutionExtender handles the extra `ExtendedSolution.xml` manifest and the pre-/post-import reconciliation that PAC does not perform for unmanaged solutions. Connections use the **DataverseConnection** NuGet package; no Daxif runtime dependency is required.

## Build and run

Requires the .NET 10 SDK. To try the unpublished package from this repository:

```bash
dotnet test
dotnet pack src/SolutionExtender.Tool -c Release -o artifacts/packages
dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- --help
```

Once published to your NuGet feed, use `dnx SolutionExtender@<version> -- <arguments>` (add `--source <feed>` for a private feed). The tag-triggered publishing workflow must be configured on GitHub and nuget.org before releases can publish. Pin versions in deployment pipelines.

For development without packing:

```bash
dotnet run --project src/SolutionExtender.Tool -- inspect --input ./out/Magnus_extended.zip
```

## Connection selection

Every Dataverse command accepts an explicit environment URL and authentication method:

```bash
az login
dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- pre-import \
  --zip ./out/Magnus_extended.zip \
  --environment https://target.crm4.dynamics.com --auth azcli
```

`--environment` is an HTTPS organization URL, **not** a PAC auth-profile name. The tool does not read or change PAC's selected environment; choose the same target explicitly for both tools.

| Option | Meaning |
| --- | --- |
| `--auth azcli` | Reuse an authenticated Azure CLI session (`az login`). |
| `--auth devicecode` | Authenticate using a device code. |
| `--auth interactive` | Interactive browser authentication; default. `browser` is an alias. |
| `--tenant-id <tenant>` | Optional tenant override for any method. |
| `--client-id <app-id>` | Optional application registration for device-code/browser authentication. Not valid with `azcli`. |

DataverseConnection's built-in credential options and persistent token caching are used by default. Explicit browser/device-code tenant/client overrides create Azure Identity options with persistent caching enabled; these use Azure Identity's encrypted storage requirements. Protect authentication caches. `azcli` depends on the Azure CLI's own token cache.

## 1. Export and capture extended metadata

Start with a fresh PAC export, then capture state, owners, and keep-lists from the **source** environment immediately after exporting. Avoid modifying that environment between export and capture.

```bash
pac solution export --name Magnus --path ./out/Magnus.zip --managed false

dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- extend \
  --zip ./out/Magnus.zip --output ./out/Magnus_extended.zip \
  --environment https://source.crm4.dynamics.com --auth azcli
```

The unique solution name is read from `solution.xml`. `extend` requires the source solution to exist, writes a separate output by default, and never modifies Dataverse. A plain ZIP cannot reliably supply live states and workflow owners; the capture step therefore needs a connection.

### Native manifest format

New captures write `ExtendedSolution.xml` using namespace **`urn:solutionextender:manifest`**, with an explicit **`version="1"`**. The contract is independent of Daxif and F# runtime serialization. Components use named attributes instead of tuples; states are a flat list rather than a serialized F# map.

```xml
<ExtendedSolution xmlns="urn:solutionextender:manifest" version="1">
  <Assemblies>
    <Component id="441b30a5-16bd-f111-aaad-000d3a31e0c8" name="Example.Plugins" />
  </Assemblies>
  <PluginTypes />
  <PluginSteps />
  <PluginImages />
  <Workflows />
  <WebResources />
  <CustomApis />
  <States>
    <State id="0cc1ad40-0636-5253-a9f5-c0bbca66f29b" logicalName="savedquery" stateCode="0" statusCode="1" />
  </States>
</ExtendedSolution>
```

Workflow components optionally include `owner="user@example.org"`. Every section except `CustomApis` is required, even when empty. Unsupported versions, unknown elements/attributes, and missing or duplicate sections are rejected before reconciliation. Output is deterministic for source-control diffs.

**Only SolutionExtender manifests are supported.** There is no legacy reader, writer, or migration path. Start with a fresh PAC export and run `extend`; use the resulting ZIP with SolutionExtender’s pre-/post-import commands.

## 2. Preserve metadata alongside PAC unpacked files

```bash
pac solution unpack --zipfile ./out/Magnus_extended.zip --folder ./solutions/Magnus --packagetype Unmanaged

dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- extract \
  --zip ./out/Magnus_extended.zip --folder ./solutions/Magnus
```

`extract` writes a validated, deterministic `ExtendedSolution.xml` sidecar at the unpacked folder root. Commit it with the PAC source. It does **not** patch PAC-generated component XML or depend on PAC's internal folder layout. Add `--overwrite` to replace an existing sidecar.

After PAC packing, explicitly attach the sidecar:

```bash
pac solution pack --folder ./solutions/Magnus --zipfile ./out/Magnus_packed.zip --packagetype Unmanaged

dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- attach \
  --zip ./out/Magnus_packed.zip --manifest ./solutions/Magnus \
  --output ./out/Magnus_extended.zip --overwrite
```

`--manifest` accepts a directory or XML file. ZIP entry payloads other than the extended manifest are preserved. Writes use temporary files and atomic replacement; duplicate extended entries are replaced with exactly one. In-place replacement requires `--overwrite`.

**Important:** keep-lists must describe the exact release being deployed. `attach` preserves the sidecar; it does not regenerate it from edited PAC files. After adding/removing components in source, refresh the export/capture (or deliberately update the manifest). Deploying stale keep-lists can delete newly added components. Workflow owners and state values cannot be inferred safely from an ordinary packed ZIP.

## 3. Reconcile and import into the target

First review the pre-import plan. By default, **no mutations occur**:

```bash
dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- pre-import \
  --zip ./out/Magnus_extended.zip \
  --environment https://target.crm4.dynamics.com --auth azcli
```

Then execute the deployment, stopping immediately if any command fails:

```bash
dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- pre-import \
  --zip ./out/Magnus_extended.zip --apply \
  --environment https://target.crm4.dynamics.com --auth azcli

pac solution import --path ./out/Magnus_extended.zip

# Run only after the import has completed successfully.
dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- post-import \
  --zip ./out/Magnus_extended.zip --apply --reassign-workflows \
  --environment https://target.crm4.dynamics.com --auth azcli

pac solution publish
```

For asynchronous PAC imports, wait for successful completion before post-import. Make sure PAC is authenticated against the **same target URL**. `--reassign-workflows` is optional and only valid for post-import. Remove `--apply` to preview either phase. `--output plan.json` writes the plan to a file instead of stdout; mutations still require `--apply`.

### Daxif behavior implemented

| Phase | Behavior / matching key |
| --- | --- |
| Pre-import | Delete obsolete custom APIs by unique name. |
| Pre-import | Delete obsolete images and active plugin steps by GUID. |
| Pre-import | Delete obsolete plugin types and assemblies by name. |
| Post-import | Delete obsolete unmanaged web resources by name. |
| Post-import | Deactivate and delete obsolete workflow definitions, including modern flow definitions, by GUID. |
| Post-import | Optionally assign workflows to target users matched by source owner's domain name, drafting before assignment and restoring state afterward. |
| Post-import | Restore saved-query and workflow state/status values from the manifest. |

Deletion order is custom APIs → images → steps → types → assemblies. Step enumeration follows Daxif's **active-step-only** behavior (`statecode=0`, `statuscode=1`); inactive standalone steps are not reconciled. Types and images are enumerated under solution assemblies and active solution steps respectively. Queries are paged and filtered to unmanaged records. Direct component queries include a solution ID filter. Standard component types also have a type filter; Custom APIs use the solution-component object-ID join without assuming an environment-specific component type number.

### Safety and limitations

- Intended for **unmanaged** solution reconciliation. Both managed source ZIPs and managed target solutions are rejected by connected commands.
- This is **destructive synchronization**, not merely removing a component from a solution. Dataverse deletions can affect other solutions referencing the same components, and deleting parent records may cascade to children. Use a dedicated release solution, review plans, and back up first.
- There is no transaction across pre-import, PAC import, and post-import. Operations run sequentially and fail fast; prior successful operations are not rolled back.
- A missing target solution is a successful pre-import no-op. Post-import requires it to exist. Authentication/query failures are not interpreted as "solution missing."
- Missing state records, missing/ambiguous enabled workflow-owner users, and team-owned source workflows fail explicitly before planned changes. Team ownership cannot be represented in Daxif's domain-name format.
- An omitted `CustomApis` section means **do not reconcile APIs**, while an explicitly empty section means delete all in-scope APIs. Fresh captures include this section.
- Unknown manifest fields, duplicate IDs, duplicate root manifests, invalid IDs, state-map key mismatches, and DTD/entity expansion are rejected rather than silently losing metadata.
- No generic entity/attribute deletion, schema synchronization, workflow impersonation, or connection-secret command-line options are included.
- No HTTP server is included: the offline service is the reusable `SolutionExtender.Core` class library, and the connected orchestration is `DataverseDeployment(IOrganizationService)`.

## Offline inspection and planning

```bash
dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- inspect --input ./out/Magnus_extended.zip

dnx SolutionExtender@0.1.0 --source ./artifacts/packages -- plan \
  --source ./release.zip --target ./target_extended.zip --phase pre-import
```

Inputs accept ZIPs, XML files, or sidecar directories. Offline plans compare extended snapshots, **not** live environment state. For post-import plans, the target snapshot must include all source state records; take the target snapshot after importing. No saved plan execution command is provided; connected commands query the target again before applying.

## Development / verification

```bash
dotnet build
dotnet test
dotnet pack src/SolutionExtender.Tool -c Release -o artifacts/packages
```

Tests use a native XML fixture and generated fresh PAC-style ZIPs to exercise XML round-trips, export → attach → extract → reattach workflows, payload preservation, unsupported-format rejection, component matching/deletion order, state restoration, scoped queries, reassignment, and fail-fast execution through a fake organization service. Live Dataverse authentication/import testing requires an environment and is not performed by the unit suite.

Reference implementations: `context-and-oss/Daxif` (`Modules/Solution/Extend.fs`, `Domain.fs`) and `delegateas/DataverseConnection`. Deployment semantics are based on Daxif; the new manifest contract is owned and versioned by SolutionExtender.


## NuGet deployment with trusted publishing

`.github/workflows/publish-nuget.yml` tests, packs, and publishes when a tag such as `v0.1.0` or `v0.2.0-preview.1` is pushed. The tag determines the package version, overriding the development version in the project. Actions are pinned to commit SHAs. The publishing job requests GitHub OIDC credentials immediately before pushing; **no long-lived NuGet API-key secret is required**.

One-time setup (requires repository administration and the NuGet package owner's account):

1. Add a GitHub repository **Actions secret** named **`NUGET_USER`**, containing your NuGet profile username (not your email address). No GitHub Actions environment or repository variable is required. Protect release tags against unauthorized creation or movement.
2. On nuget.org, create a **Trusted Publishing** policy owned by the intended package owner, with:

   | Field | Value |
   | --- | --- |
   | Repository owner | `context-and-oss` |
   | Repository | `SolutionExtender` |
   | Workflow file | `publish-nuget.yml` (filename only) |
   | Environment | Leave empty (no GitHub Actions environment) |
   | Package pattern | `SolutionExtender` |

   Enable the publishing scopes needed for new versions; for the first publication, also permit publishing a new package. The package name must be available or already owned by that account/organization.
3. Merge the release code, then push the release tag:

   ```bash
   git tag v0.1.0
   git push origin v0.1.0
   ```

The workflow uploads the built package as an artifact before authentication/publishing. Re-publishing an existing NuGet version fails deliberately (no `--skip-duplicate`); use a new release tag/version instead. GitHub/NuGet policy configuration is external to the repository and is not created by the workflow.

## Live read-only verification

On October 2, 2026, device-code authentication through DataverseConnection was tested against `abs-preview.crm.dynamics.com`. The live solution was verified as **Magnus**, unmanaged, publisher **ContextAnd**, prefix **ctx**, version **1.0.0.1**. Metadata capture and both deployment planning phases were exercised without `--apply`. Local captures and plans are under ignored `artifacts/live-test/`, not committed. No live import, deletion, state update, or workflow reassignment was performed.
