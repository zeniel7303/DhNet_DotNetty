#!/bin/bash
# Stop hook: Claude 응답 완료 후 C# 빌드 자동 검사
# C# 파일이 수정된 세션에서만 실행됩니다.

set -e

# Stop 이벤트 데이터 읽기
stop_data=$(cat)
session_id=$(echo "$stop_data" | jq -r '.session_id // empty')

cache_dir="$CLAUDE_PROJECT_DIR/.claude/tsc-cache/${session_id:-default}"

# 이번 세션에서 C# 파일이 수정되지 않았으면 종료
if [[ ! -f "$cache_dir/edited-files.log" ]]; then
    exit 0
fi

if ! grep -qE '\.(cs|proto)' "$cache_dir/edited-files.log" 2>/dev/null; then
    exit 0
fi

# dotnet 실행 파일 탐색
DOTNET_CMD=""
if command -v dotnet &>/dev/null; then
    DOTNET_CMD="dotnet"
elif [[ -f "$HOME/.dotnet/dotnet.exe" ]]; then
    DOTNET_CMD="$HOME/.dotnet/dotnet.exe"
elif [[ -f "/c/Program Files/dotnet/dotnet.exe" ]]; then
    DOTNET_CMD="/c/Program Files/dotnet/dotnet.exe"
fi

# dotnet을 찾지 못하면 빌드 명령만 안내
if [[ -z "$DOTNET_CMD" ]]; then
    dotnet_cmds=$(grep ':dotnet:' "$cache_dir/commands.txt" 2>/dev/null | sed 's/^[^:]*:[^:]*://' | sort -u)
    if [[ -n "$dotnet_cmds" ]]; then
        echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
        echo "🔨 C# 파일 수정됨 — 빌드 확인 권장"
        while IFS= read -r cmd; do
            echo "   $cmd"
        done <<< "$dotnet_cmds"
        echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    fi
    exit 0
fi

# 수정된 C# 프로젝트 빌드 실행
build_failed=false

# commands.txt에서 dotnet 빌드 명령 추출 (형식: repo:dotnet:command)
while IFS= read -r line; do
    cmd=$(echo "$line" | sed 's/^[^:]*:[^:]*://')
    if [[ -z "$cmd" ]]; then continue; fi

    # csproj 경로 추출 (따옴표 제거)
    csproj=$(echo "$cmd" | grep -oE '"[^"]+"' | tr -d '"' | head -1)
    if [[ -z "$csproj" ]]; then continue; fi

    proj_name=$(basename "$csproj")
    echo "🔨 빌드 중: $proj_name"

    if "$DOTNET_CMD" build "$csproj" --nologo -v quiet 2>&1; then
        echo "✅ $proj_name 빌드 성공"
    else
        echo "❌ $proj_name 빌드 실패"
        build_failed=true
    fi
done < <(grep ':dotnet:' "$cache_dir/commands.txt" 2>/dev/null | sort -u)

# 빌드 실패 시 exit 2: Claude에게 오류 메시지 표시 (다음 응답 차단)
if [[ "$build_failed" == "true" ]]; then
    echo ""
    echo "빌드 오류가 있습니다. 수정 후 다시 확인하세요."
    exit 2
fi

exit 0
