#!/bin/bash
set -e

export PATH="/c/Program Files/nodejs:$PATH"

cd "$CLAUDE_PROJECT_DIR/.claude/hooks"
cat | npx tsx skill-activation-prompt.ts
