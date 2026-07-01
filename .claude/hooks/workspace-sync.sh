#!/bin/bash
# workspace-sync.sh — post-commit hook
# 작업 문서(dev/)와 Claude 메모리를 claude-workspace 레포에 동기화
#
# 사전 조건:
#   git config --global claude.workspace "/path/to/claude-workspace"

WORKSPACE=$(git config --global claude.workspace 2>/dev/null)
if [ -z "$WORKSPACE" ]; then
    exit 0
fi

PROJECT_KEY=$(git config claude.projectKey 2>/dev/null)
MEMORY_PATH=$(git config claude.memoryPath 2>/dev/null)

if [ -z "$PROJECT_KEY" ] || [ -z "$MEMORY_PATH" ]; then
    echo "[workspace-sync] claude.projectKey 또는 claude.memoryPath 미설정. install-hooks.sh 실행 필요."
    exit 0
fi

TARGET="$WORKSPACE/$PROJECT_KEY"
REPO_ROOT=$(git rev-parse --show-toplevel)

sync_dir() {
    local src="$1"
    local dst="$2"
    [ -d "$src" ] || return 0
    rm -rf "$dst"
    cp -r "$src" "$dst"
    rm -rf "$dst/.git"
}

# dev/ 동기화
sync_dir "$REPO_ROOT/dev" "$TARGET/dev"

# Claude 메모리 동기화
sync_dir "$MEMORY_PATH" "$TARGET/memory"

# settings.local.json 동기화 (있는 경우)
if [ -f "$REPO_ROOT/.claude/settings.local.json" ]; then
    cp "$REPO_ROOT/.claude/settings.local.json" "$TARGET/settings.local.json"
fi

# workspace 레포에 커밋 & 푸시
cd "$WORKSPACE" || exit 0
git add "$PROJECT_KEY/"
if git diff --cached --quiet; then
    exit 0
fi
git commit -m "sync: $PROJECT_KEY $(date '+%Y-%m-%d %H:%M')"
git push
