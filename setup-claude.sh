#!/bin/bash
# Claude workspace 초기 설정 — 새 PC에서 1회 실행
WORKSPACE_URL="https://github.com/zeniel7303/claude-workspace"
WORKSPACE_PATH="${1:-$(dirname "$(git rev-parse --show-toplevel)")/claude-workspace}"
PROJECT_DIR="$(git rev-parse --show-toplevel)"

[ -d "$WORKSPACE_PATH/.git" ] || git clone "$WORKSPACE_URL" "$WORKSPACE_PATH"
bash "$WORKSPACE_PATH/DhNet_DotNetty/setup.sh" "$PROJECT_DIR"
