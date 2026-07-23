# Публикация на nuget.org (Trusted Publishing)

Долгоживущий API key не нужен: [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishers) + GitHub OIDC.

## 1. Политика на nuget.org

1. Войти на [nuget.org](https://www.nuget.org/) (владелец пакета, напр. **LonelySoul**).
2. **Профиль** → **Trusted Publishing** → GitHub:
   - **Repository owner:** `AI-Guiders`
   - **Repository:** `cdp-scriptable-ide`
   - **Workflow file:** `nuget-publish.yml`
   - **Environment:** пусто

## 2. Запуск

- **Тег:** `v0.1.48` → пакет `0.1.48`
- **Вручную:** Actions → **Publish to NuGet** → version `0.1.48`

## 3. Локально (проверка pack)

```bash
dotnet pack Cdp.ScriptableIde.csproj -c Release -o nupkg
```
