#!/bin/bash
# 建置 macOS 版：dotnet publish（自帶執行階段）→ 組成 .app → ad-hoc 簽章 → 產生 .dmg 安裝映像。
# 用法：packaging/macos/build-macos.sh [osx-arm64|osx-x64]（預設依本機架構）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
APP_NAME="SpeakerHeadphoneSwitch"
DISPLAY_NAME="音訊快速切換"
# 版本號以 csproj 的 <Version> 為準，可用 VERSION 環境變數覆寫。
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/$APP_NAME.csproj")}"

if [[ $# -ge 1 ]]; then
    RID="$1"
elif [[ "$(uname -m)" == "arm64" ]]; then
    RID="osx-arm64"
else
    RID="osx-x64"
fi

DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
if [[ ! -x "$DOTNET" ]]; then
    echo "找不到 dotnet，請先安裝 .NET 8 SDK。" >&2
    exit 1
fi

OUT="$ROOT/dist/macos"
# 發佈輸出只是中繼檔；直接在 Finder 點兩下裡面的 Unix 執行檔會開出終端機視窗，所以放在暫存目錄。
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
PUBLISH_DIR="$WORK_DIR/publish"
APP_DIR="$OUT/$APP_NAME.app"
DMG_PATH="$OUT/$APP_NAME-$VERSION-$RID.dmg"

# LaunchServices 以路徑快取 App 圖示；同一路徑重建時先取消登記，避免 Finder 沿用舊的（無圖示）快取。
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
[[ -d "$APP_DIR" ]] && "$LSREGISTER" -u "$APP_DIR" 2>/dev/null || true
rm -rf "$OUT"/publish-* "$APP_DIR" "$DMG_PATH"
mkdir -p "$OUT"

echo "== dotnet publish ($RID) =="
"$DOTNET" publish "$ROOT/$APP_NAME.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:UseAppHost=true \
    -p:Version="$VERSION" \
    -o "$PUBLISH_DIR"

echo "== 組成 $APP_NAME.app =="
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp -R "$PUBLISH_DIR/" "$APP_DIR/Contents/MacOS/"
# 符號檔不需要隨安裝包散佈。
find "$APP_DIR/Contents/MacOS" -name "*.pdb" -delete
sed -e "s/__VERSION__/$VERSION/g" \
    -e "s/__EXECUTABLE__/$APP_NAME/g" \
    -e "s/__DISPLAY_NAME__/$DISPLAY_NAME/g" \
    "$SCRIPT_DIR/Info.plist" > "$APP_DIR/Contents/Info.plist"
if [[ -f "$SCRIPT_DIR/AppIcon.icns" ]]; then
    cp "$SCRIPT_DIR/AppIcon.icns" "$APP_DIR/Contents/Resources/AppIcon.icns"
fi
chmod +x "$APP_DIR/Contents/MacOS/$APP_NAME"

echo "== ad-hoc 簽章 =="
# Apple Silicon 必須簽章才能執行；沒有 Developer ID 時使用 ad-hoc 簽章。
codesign --force --deep --sign "${CODESIGN_IDENTITY:--}" "$APP_DIR"
codesign --verify --deep --strict "$APP_DIR"
"$LSREGISTER" -f "$APP_DIR" 2>/dev/null || true

echo "== 產生 DMG =="
STAGING="$WORK_DIR/staging"
mkdir -p "$STAGING"
cp -R "$APP_DIR" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
hdiutil create \
    -volname "$DISPLAY_NAME" \
    -srcfolder "$STAGING" \
    -ov -format UDZO \
    "$DMG_PATH" >/dev/null

echo
echo "完成："
echo "  App: $APP_DIR"
echo "  DMG: $DMG_PATH"
