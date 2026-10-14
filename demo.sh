#!/usr/bin/env bash
#
# Build your first Microsoft Foundry autopilot - guided demo runner.
#
# Follows the Microsoft Learn quickstart "Build your first autopilot":
#   https://learn.microsoft.com/azure/foundry/agents/how-to/agent-365
# using the official sample in microsoft-foundry/foundry-samples.
#
# No Docker needed: the sample builds the agent's container image in Azure
# with ACR Tasks (az acr build), so nothing is built on this machine.
# You do need PowerShell 7 (pwsh): the sample's azd hooks are PowerShell scripts.
#
# Every command is echoed before it runs, so the audience can follow along.

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: ./demo.sh [--check | --dry-run | --help]

  (no option)  Full run: get the sample, sign in, preview, then
               provision + build + publish with a single azd provision.
  --check      Pre-flight checks only (required tools installed).
               No sign-in and no changes.
  --dry-run    Print every command the demo would run, without running it.
               Useful for rehearsing without an eligible tenant.
  --help       Show this help.

Environment variables:
  TENANT_ID              Microsoft Entra tenant ID (required for a full run)
  SAMPLE_LANG            python (default) or csharp
  AZD_ENV_NAME           azd environment name (default: autopilot-demo)
  AZURE_LOCATION         Region for a NEW azd environment. Must support
                         Foundry hosted agents (for example: uksouth)
  AZURE_SUBSCRIPTION_ID  Subscription for a NEW azd environment
  ASSUME_YES             Set to 1 to skip the confirmation after the preview
  USE_DEVICE_CODE        Set to 1 to sign in with a device code instead of a
                         browser (for example in Azure Cloud Shell)

Docker is not required: the image is built in Azure with ACR Tasks.
EOF
}

MODE="run"
case "${1:-}" in
  "") ;;
  --check) MODE="check" ;;
  --dry-run) MODE="dry-run" ;;
  -h | --help)
    usage
    exit 0
    ;;
  *)
    usage >&2
    printf '\nERROR: unknown option %s\n' "$1" >&2
    exit 2
    ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_URL="https://github.com/microsoft-foundry/foundry-samples.git"
CLONE_DIR="${SCRIPT_DIR}/foundry-samples"
SAMPLE_LANG="${SAMPLE_LANG:-python}"
AZD_ENV_NAME="${AZD_ENV_NAME:-autopilot-demo}"
MIN_AZD_VERSION="1.27.1" # minimum stated in the Python sample README

step() { printf '\n==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '    WARNING: %s\n' "$*" >&2; }
fail() {
  printf '\nERROR: %s\n' "$*" >&2
  exit 1
}

# Echo a command (copy-pasteable), then run it unless --dry-run.
run() {
  local arg shown="" needs_quotes='[[:space:]{}*?$<>|;&]'
  for arg in "$@"; do
    if [[ "${arg}" =~ ${needs_quotes} ]]; then
      shown="${shown} \"${arg}\""
    else
      shown="${shown} ${arg}"
    fi
  done
  printf '    $%s\n' "${shown}"
  if [[ "${MODE}" != "dry-run" ]]; then
    "$@"
  fi
}

# version_ge A B -> success when version A >= version B (x.y.z).
version_ge() {
  local a1 a2 a3 b1 b2 b3
  IFS=. read -r a1 a2 a3 <<<"$1"
  IFS=. read -r b1 b2 b3 <<<"$2"
  a1=${a1:-0} a2=${a2:-0} a3=${a3:-0}
  b1=${b1:-0} b2=${b2:-0} b3=${b3:-0}
  if ((a1 != b1)); then
    ((a1 > b1))
    return
  fi
  if ((a2 != b2)); then
    ((a2 > b2))
    return
  fi
  ((a3 >= b3))
}

case "${SAMPLE_LANG}" in
  python | csharp) ;;
  *) fail "SAMPLE_LANG must be 'python' or 'csharp' (got '${SAMPLE_LANG}')." ;;
esac
SAMPLE_REL="samples/${SAMPLE_LANG}/foundry-autopilot-agent"
SAMPLE_DIR="${CLONE_DIR}/${SAMPLE_REL}"

# ---------------------------------------------------------------------------
# 1. Pre-flight checks
# ---------------------------------------------------------------------------
step "Pre-flight checks"
# pwsh runs the sample's azd hooks (PowerShell 7). The C# sample also runs
# 'dotnet publish' locally before the cloud build.
required=(git az azd pwsh)
if [[ "${SAMPLE_LANG}" == "csharp" ]]; then
  required+=(dotnet)
fi

missing=""
for tool in "${required[@]}"; do
  if command -v "${tool}" >/dev/null 2>&1; then
    info "ok       ${tool}"
  else
    info "MISSING  ${tool}"
    missing="${missing} ${tool}"
  fi
done
info "n/a      Docker - not needed, the image is built in Azure (ACR Tasks)"

if command -v azd >/dev/null 2>&1; then
  azd_version="$(azd version 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+' | head -n 1 || true)"
  if [[ -z "${azd_version}" ]]; then
    warn "Could not read the azd version."
  elif version_ge "${azd_version}" "${MIN_AZD_VERSION}"; then
    info "ok       azd ${azd_version}"
  else
    warn "azd ${azd_version} found; the Python sample asks for ${MIN_AZD_VERSION} or later."
  fi
fi

if [[ "${SAMPLE_LANG}" == "csharp" ]] && command -v dotnet >/dev/null 2>&1; then
  sdk_major="$(dotnet --list-sdks 2>/dev/null | sed -n 's/^\([0-9][0-9]*\)\..*/\1/p' | sort -n | tail -n 1 || true)"
  if [[ -z "${sdk_major}" || "${sdk_major}" -lt 9 ]]; then
    warn "The C# sample README asks for the .NET 9.0 SDK (newest SDK found: ${sdk_major:-none})."
  else
    info "ok       .NET SDK ${sdk_major}.x"
  fi
fi

if [[ -n "${missing}" ]]; then
  if [[ "${MODE}" == "dry-run" ]]; then
    warn "Missing:${missing} (ignored in --dry-run)."
  else
    fail "Missing prerequisites:${missing}. Install or start them, then try again."
  fi
fi

if [[ "${MODE}" == "check" ]]; then
  step "Pre-flight checks passed. Set TENANT_ID and run ./demo.sh to start."
  exit 0
fi

if [[ "${MODE}" == "dry-run" ]]; then
  TENANT_ID="${TENANT_ID:-<tenant-id>}"
  step "DRY RUN - commands are printed, not executed"
fi

[[ -n "${TENANT_ID:-}" ]] ||
  fail "Set TENANT_ID first, for example: export TENANT_ID=\"00000000-0000-0000-0000-000000000000\""

# ---------------------------------------------------------------------------
# 2. Get the official sample (sparse, shallow clone of just the sample folder)
# ---------------------------------------------------------------------------
step "Getting the ${SAMPLE_LANG} autopilot sample"
if [[ -d "${CLONE_DIR}/.git" ]]; then
  info "Reusing the existing checkout in ${CLONE_DIR}"
elif ! run git clone --depth 1 --filter=blob:none --sparse "${REPO_URL}" "${CLONE_DIR}"; then
  warn "Sparse clone failed; falling back to a regular shallow clone."
  rm -rf "${CLONE_DIR}"
  run git clone --depth 1 "${REPO_URL}" "${CLONE_DIR}"
fi

if [[ "${MODE}" == "dry-run" ]]; then
  run git -C "${CLONE_DIR}" sparse-checkout add "${SAMPLE_REL}"
  info "\$ cd ${SAMPLE_DIR}"
else
  if [[ "$(git -C "${CLONE_DIR}" config --get core.sparseCheckout || true)" == "true" ]]; then
    run git -C "${CLONE_DIR}" sparse-checkout add "${SAMPLE_REL}"
  fi
  [[ -d "${SAMPLE_DIR}" ]] || fail "Sample folder not found: ${SAMPLE_DIR}"
  cd "${SAMPLE_DIR}" || fail "Cannot open ${SAMPLE_DIR}"
  info "Working in ${SAMPLE_DIR}"
fi

# ---------------------------------------------------------------------------
# 3. Sign in with both CLIs (same tenant)
# ---------------------------------------------------------------------------
step "Signing in with Azure CLI and Azure Developer CLI"
az_login_args=(--tenant "${TENANT_ID}")
azd_login_args=(--tenant-id "${TENANT_ID}")
if [[ "${USE_DEVICE_CODE:-0}" == "1" ]]; then
  az_login_args+=(--use-device-code)
  azd_login_args+=(--use-device-code)
fi
run az login "${az_login_args[@]}"
run azd auth login "${azd_login_args[@]}"

step "Checkpoint: confirm the tenant and subscription"
run az account show --query "{subscription:name, subscriptionId:id, tenantId:tenantId}" --output table

# ---------------------------------------------------------------------------
# 4. azd environment
# ---------------------------------------------------------------------------
step "Selecting the azd environment '${AZD_ENV_NAME}'"
new_env_args=("${AZD_ENV_NAME}")
if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
  new_env_args+=(--subscription "${AZURE_SUBSCRIPTION_ID}")
fi
if [[ -n "${AZURE_LOCATION:-}" ]]; then
  new_env_args+=(--location "${AZURE_LOCATION}")
fi

if [[ "${MODE}" == "dry-run" ]]; then
  run azd env select "${AZD_ENV_NAME}"
  info "(or, the first time)"
  run azd env new "${new_env_args[@]}"
elif azd env select "${AZD_ENV_NAME}" >/dev/null 2>&1; then
  info "Using the existing environment '${AZD_ENV_NAME}'"
else
  run azd env new "${new_env_args[@]}"
fi

if [[ "${SAMPLE_LANG}" == "python" ]]; then
  info "The Python sample README sets public network access before provisioning:"
  run azd env set PUBLIC_NETWORK_ACCESS Enabled
fi

# ---------------------------------------------------------------------------
# 5. Preview before committing
# ---------------------------------------------------------------------------
step "Preview: what the template will create (no changes yet)"
run azd provision --preview

if [[ "${MODE}" != "dry-run" && "${ASSUME_YES:-0}" != "1" ]]; then
  echo
  info "Checkpoint - before continuing, confirm:"
  info "  * correct tenant and subscription"
  info "  * a region that supports hosted agents"
  info "  * the expected resources and role assignments"
  answer=""
  read -r -p "    Continue with provisioning, build and publishing? [y/N] " answer || true
  case "${answer}" in
    y | Y | yes | YES) ;;
    *)
      step "Stopped before provisioning. Nothing was deployed."
      exit 0
      ;;
  esac
fi

# ---------------------------------------------------------------------------
# 6. One command, three stages: provision -> build blueprint -> publish
# ---------------------------------------------------------------------------
step "Provisioning infrastructure, building the agent in Azure (ACR Tasks) and publishing the blueprint"
run azd provision

step "Deployment values - look for the agent name, agent version and blueprint ID"
run azd env get-values

if [[ "${MODE}" == "dry-run" ]]; then
  step "Dry run complete - nothing was executed. After a real run:"
else
  step "Azure side complete. A publish request should now be waiting for approval."
fi

cat <<EOF

Next - Microsoft 365 administrator (Global Administrator or AI Administrator):
    Microsoft 365 admin center > Agents > All agents > Requests
    Open the autopilot (state: Pending activate) and complete the
    Publish new agent wizard. Registry status should become Available.

Then - create your instance (you must be in the hiring scope):
    Teams:   Apps > Agents for your team > select the autopilot > Create instance
    Copilot: Agents > Agents for your team > select the autopilot > Create instance

Clean up when finished (from ${SAMPLE_DIR}):
    azd down
EOF
