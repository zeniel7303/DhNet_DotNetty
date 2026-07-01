#!/bin/bash
# install-hooks.sh — 새 PC에서 1회 실행
# claude-workspace 동기화 훅을 설치하고 프로젝트 설정을 저장

PROJECT_KEY="DhNet_DotNetty"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

derive_memory_key() {
    local path="$1"
    path="${path#/}"
    path="${path/:/}"
    local first_upper
    first_upper=$(echo "${path:0:1}" | tr 'a-z' 'A-Z')
    local rest="${path:1}"
    path="${first_upper}${rest}"
    path="${path/\//"--"}"
    path="${path//\//-}"
    path="${path//_/-}"
    echo "$path"
}

MEMORY_KEY=$(derive_memory_key "$REPO_ROOT")
USERPROFILE_UNIX=$(echo "$USERPROFILE" | sed 's|\\|/|g')
MEMORY_PATH="$USERPROFILE_UNIX/.claude/projects/$MEMORY_KEY/memory"

git -C "$REPO_ROOT" config claude.projectKey "$PROJECT_KEY"
git -C "$REPO_ROOT" config claude.memoryPath "$MEMORY_PATH"

cp "$SCRIPT_DIR/hooks/workspace-sync.sh"    "$REPO_ROOT/.git/hooks/post-commit"
cp "$SCRIPT_DIR/hooks/workspace-restore.sh" "$REPO_ROOT/.git/hooks/post-merge"
chmod +x "$REPO_ROOT/.git/hooks/post-commit"
chmod +x "$REPO_ROOT/.git/hooks/post-merge"

echo "✓ Hooks installed for $PROJECT_KEY"
echo "  Repo root  : $REPO_ROOT"
echo "  Memory key : $MEMORY_KEY"
echo "  Memory path: $MEMORY_PATH"
echo ""
echo "  workspace 경로를 아직 설정하지 않았다면:"
echo "  git config --global claude.workspace \"/path/to/claude-workspace\""
