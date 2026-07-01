#!/bin/bash
# workspace-restore.sh — post-merge hook
# 다른 PC에서 push된 변경사항을 로컬로 복원

WORKSPACE=$(git config --global claude.workspace 2>/dev/null)
if [ -z "$WORKSPACE" ]; then
    exit 0
fi

PROJECT_KEY=$(git config claude.projectKey 2>/dev/null)
MEMORY_PATH=$(git config claude.memoryPath 2>/dev/null)

if [ -z "$PROJECT_KEY" ] || [ -z "$MEMORY_PATH" ]; then
    exit 0
fi

SOURCE="$WORKSPACE/$PROJECT_KEY"
REPO_ROOT=$(git rev-parse --show-toplevel)

# workspace 최신화
cd "$WORKSPACE" && git pull --quiet

restore_dir() {
    local src="$1"
    local dst="$2"
    [ -d "$src" ] || return 0
    rm -rf "$dst"
    cp -r "$src" "$dst"
}

# dev/ 복원
restore_dir "$SOURCE/dev" "$REPO_ROOT/dev"

# Claude 메모리 복원
restore_dir "$SOURCE/memory" "$MEMORY_PATH"

# settings.local.json 복원
if [ -f "$SOURCE/settings.local.json" ]; then
    cp "$SOURCE/settings.local.json" "$REPO_ROOT/.claude/settings.local.json"
fi
