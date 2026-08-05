#!/bin/bash

set -euo pipefail

configuration="Release"
version="1.0.0"
runtime=""
python_version="3.12.13"
python_build_release="20260805"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --configuration)
      configuration="$2"
      shift 2
      ;;
    --version)
      version="$2"
      shift 2
      ;;
    --runtime)
      runtime="$2"
      shift 2
      ;;
    --python-version)
      python_version="$2"
      shift 2
      ;;
    --python-build-release)
      python_build_release="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.][0-9]+)?$ ]]; then
  echo "Version must contain three or four numeric components: $version" >&2
  exit 2
fi

host_architecture="$(uname -m)"
if [[ -z "$runtime" ]]; then
  case "$host_architecture" in
    arm64) runtime="osx-arm64" ;;
    x86_64) runtime="osx-x64" ;;
    *)
      echo "Unsupported macOS architecture: $host_architecture" >&2
      exit 2
      ;;
  esac
fi

case "$runtime" in
  osx-arm64)
    python_architecture="aarch64"
    expected_host_architecture="arm64"
    ;;
  osx-x64)
    python_architecture="x86_64"
    expected_host_architecture="x86_64"
    ;;
  *)
    echo "Runtime must be osx-arm64 or osx-x64: $runtime" >&2
    exit 2
    ;;
esac

if [[ "$host_architecture" != "$expected_host_architecture" ]]; then
  echo "Native Tesseract packaging requires a $expected_host_architecture build host for $runtime." >&2
  exit 2
fi

for command_name in dotnet curl tar pkgbuild productbuild dylibbundler tesseract; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required installer build command was not found: $command_name" >&2
    exit 1
  fi
done

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_directory/.." && pwd)"
artifacts_root="$repository_root/artifacts"
build_root="$artifacts_root/mac-installer-build/$runtime"
output_root="$artifacts_root/installer"
cache_root="$artifacts_root/cache"
app_bundle="$build_root/DOC2MD.app"
contents_root="$app_bundle/Contents"
macos_root="$contents_root/MacOS"
resources_root="$contents_root/Resources"
publish_root="$build_root/publish"
package_root="$build_root/package-root"
component_package="$build_root/DOC2MD-component.pkg"
output_package="$output_root/DOC2MD-$version-$runtime.pkg"

case "$build_root" in
  "$artifacts_root"/mac-installer-build/*) ;;
  *)
    echo "Refusing to reset unexpected build directory: $build_root" >&2
    exit 1
    ;;
esac

rm -rf "$build_root"
mkdir -p "$macos_root" "$resources_root" "$publish_root" "$package_root/Applications" "$package_root/usr/local/bin" "$output_root" "$cache_root"

publish_project() {
  local project_name="$1"
  local project_path="$2"
  local project_output="$publish_root/$project_name"

  dotnet publish "$project_path" \
    --nologo \
    --configuration "$configuration" \
    --runtime "$runtime" \
    --self-contained true \
    --output "$project_output" \
    -p:Version="$version" \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:DebugSymbols=false \
    -p:DebugType=None \
    -p:ContinuousIntegrationBuild=true \
    -p:Deterministic=true

  cp -R "$project_output/." "$macos_root/"
}

publish_project cli "$repository_root/src/DOC2MD.Cli/DOC2MD.Cli.csproj"
publish_project gui "$repository_root/src/DOC2MD.Gui/DOC2MD.Gui.csproj"
publish_project api "$repository_root/src/DOC2MD.Api/DOC2MD.Api.csproj"
publish_project mcp "$repository_root/src/DOC2MD.Mcp/DOC2MD.Mcp.csproj"

for executable in DOC2MD.Cli DOC2MD.Gui DOC2MD.Api DOC2MD.Mcp; do
  if [[ ! -x "$macos_root/$executable" ]]; then
    echo "The published frontend executable was not produced: $executable" >&2
    exit 1
  fi
done

if [[ -d "$macos_root/Resources/markitdown" ]]; then
  mv "$macos_root/Resources/markitdown" "$resources_root/markitdown"
  rmdir "$macos_root/Resources"
else
  echo "The published MarkItDown resources were not produced." >&2
  exit 1
fi

python_asset="cpython-$python_version+$python_build_release-$python_architecture-apple-darwin-install_only.tar.gz"
python_archive="$cache_root/$python_asset"
python_url="https://github.com/astral-sh/python-build-standalone/releases/download/$python_build_release/$python_asset"
if [[ ! -s "$python_archive" ]]; then
  curl --fail --location --retry 3 "$python_url" --output "$python_archive"
fi
tar -xzf "$python_archive" -C "$resources_root"

bundled_python="$resources_root/python/bin/python3"
if [[ ! -x "$bundled_python" ]]; then
  echo "The standalone Python archive did not produce $bundled_python" >&2
  exit 1
fi

"$bundled_python" -m pip install \
  --disable-pip-version-check \
  --no-cache-dir \
  --upgrade \
  hatchling
"$bundled_python" -m pip install \
  --disable-pip-version-check \
  --no-cache-dir \
  --no-build-isolation \
  --upgrade \
  "$resources_root/markitdown[all]"

mkdir -p "$resources_root/tessdata"
for language in eng pol; do
  model_path="$cache_root/$language.traineddata"
  if [[ ! -s "$model_path" ]]; then
    curl --fail --location --retry 3 \
      "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/$language.traineddata" \
      --output "$model_path"
  fi
  cp "$model_path" "$resources_root/tessdata/$language.traineddata"
done

tesseract_source="$(command -v tesseract)"
mkdir -p "$resources_root/tesseract/bin"
cp "$tesseract_source" "$resources_root/tesseract/bin/tesseract"
chmod 755 "$resources_root/tesseract/bin/tesseract"
dylibbundler \
  -od \
  -b \
  -x "$resources_root/tesseract/bin/tesseract" \
  -d "$resources_root/tesseract/lib" \
  -p '@executable_path/../lib/'

sed "s/__DOC2MD_VERSION__/$version/g" "$script_directory/macos/Info.plist" > "$contents_root/Info.plist"
cp "$repository_root/README.md" "$resources_root/README.md"

soffice_path=""
for candidate in \
  "/Applications/LibreOffice.app/Contents/MacOS/soffice" \
  "/opt/homebrew/bin/soffice" \
  "/usr/local/bin/soffice"; do
  if [[ -x "$candidate" ]]; then
    soffice_path="$candidate"
    break
  fi
done
if [[ -z "$soffice_path" ]] && command -v soffice >/dev/null 2>&1; then
  soffice_path="$(command -v soffice)"
fi
if [[ -z "$soffice_path" ]]; then
  echo "LibreOffice is required to build and smoke-test the macOS installer." >&2
  exit 1
fi

DOC2MD_SOFFICE_PATH="$soffice_path" "$macos_root/DOC2MD.Cli" check-dependencies --json

mcp_response="$(printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  | DOC2MD_CLI_PATH="$macos_root/DOC2MD.Cli" "$macos_root/DOC2MD.Mcp")"
if [[ "$mcp_response" != *'"protocolVersion":"2024-11-05"'* ]]; then
  echo "The packaged MCP server did not complete its initialization smoke test." >&2
  exit 1
fi

smoke_input="$build_root/smoke-input.txt"
smoke_output="$build_root/smoke-output.md"
printf '%s\n' 'DOC2MD macOS dependency smoke test.' > "$smoke_input"
DOC2MD_SOFFICE_PATH="$soffice_path" "$macos_root/DOC2MD.Cli" convert \
  --input "$smoke_input" \
  --output "$smoke_output" \
  --overwrite \
  --json
if [[ ! -s "$smoke_output" ]]; then
  echo "The macOS MarkItDown smoke test did not produce output." >&2
  exit 1
fi

ocr_input="$repository_root/lib/packages/markitdown-ocr/tests/ocr_test_data/pdf_scanned_minimal.pdf"
ocr_output="$build_root/ocr-smoke-output.md"
DOC2MD_SOFFICE_PATH="$soffice_path" "$macos_root/DOC2MD.Cli" convert \
  --input "$ocr_input" \
  --output "$ocr_output" \
  --overwrite \
  --pdf-processing local \
  --ocr-languages eng+pol \
  --json
if [[ ! -s "$ocr_output" ]]; then
  echo "The macOS native Tesseract smoke test did not produce output." >&2
  exit 1
fi

if [[ -n "${DOC2MD_CODESIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --timestamp --sign "$DOC2MD_CODESIGN_IDENTITY" "$app_bundle"
  codesign --verify --deep --strict --verbose=2 "$app_bundle"
fi

cp -R "$app_bundle" "$package_root/Applications/DOC2MD.app"
ln -s "/Applications/DOC2MD.app/Contents/MacOS/DOC2MD.Cli" "$package_root/usr/local/bin/doc2md"
ln -s "/Applications/DOC2MD.app/Contents/MacOS/DOC2MD.Api" "$package_root/usr/local/bin/doc2md-api"
ln -s "/Applications/DOC2MD.app/Contents/MacOS/DOC2MD.Mcp" "$package_root/usr/local/bin/doc2md-mcp"
chmod 755 "$script_directory/macos/scripts/preinstall"

pkgbuild_arguments=(
  --root "$package_root"
  --scripts "$script_directory/macos/scripts"
  --identifier "com.taskscape.doc2md"
  --version "$version"
  --install-location "/"
)
if [[ -n "${DOC2MD_INSTALLER_SIGN_IDENTITY:-}" ]]; then
  pkgbuild_arguments+=(--sign "$DOC2MD_INSTALLER_SIGN_IDENTITY")
fi
pkgbuild "${pkgbuild_arguments[@]}" "$component_package"

productbuild_arguments=(--package "$component_package")
if [[ -n "${DOC2MD_INSTALLER_SIGN_IDENTITY:-}" ]]; then
  productbuild_arguments+=(--sign "$DOC2MD_INSTALLER_SIGN_IDENTITY")
fi
productbuild "${productbuild_arguments[@]}" "$output_package"
pkgutil --check-signature "$output_package" || true

digest="$(shasum -a 256 "$output_package" | awk '{print $1}')"
printf '%s  %s\n' "$digest" "$(basename "$output_package")" > "$output_package.sha256"

echo "macOS installer created: $output_package"
