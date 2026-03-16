# C# 비동기 프로그래밍 패턴
# C# Async Programming Patterns

## async/await 기본

```csharp
// ✅ Good
public async Task<Player> LoadPlayerAsync(int playerId)
{
    var data = await _database.GetPlayerDataAsync(playerId);
    return new Player(data);
}

// ❌ Bad: async void (이벤트 핸들러 외에는 사용 금지)
public async void LoadPlayer(int playerId)
{
    // 예외가 전파되지 않음!
}
```

## ConfigureAwait

```csharp
// 라이브러리 코드에서는 ConfigureAwait(false) 사용
public async Task<Data> GetDataAsync()
{
    return await _httpClient.GetAsync(url).ConfigureAwait(false);
}
```

## Task 취소

```csharp
public async Task ProcessAsync(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await DoWorkAsync(cancellationToken);
        await Task.Delay(1000, cancellationToken);
    }
}
```

## 베스트 프랙티스
- async 메서드 이름은 Async 접미사 사용
- Task를 반환하는 메서드는 항상 await 사용
- async void는 이벤트 핸들러에만 사용
- CancellationToken으로 취소 지원
