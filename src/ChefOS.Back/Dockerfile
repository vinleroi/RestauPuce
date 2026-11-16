# Image du site ChefOS.Back. À construire depuis la racine du dépôt :
#   docker build -f src/ChefOS.Back/Dockerfile -t chefos-back .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Les fichiers de projet d'abord : la restauration des paquets reste en cache tant qu'ils ne changent pas.
COPY src/ChefOS.Core/ChefOS.Core.csproj src/ChefOS.Core/
COPY src/ChefOS.Infrastructure/ChefOS.Infrastructure.csproj src/ChefOS.Infrastructure/
COPY src/ChefOS.Back/ChefOS.Back.csproj src/ChefOS.Back/
RUN dotnet restore src/ChefOS.Back/ChefOS.Back.csproj
COPY src/ src/
RUN dotnet publish src/ChefOS.Back/ChefOS.Back.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# curl sert uniquement à la sonde de santé de la composition (l'image d'exécution n'en contient pas).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# L'image aspnet fournit déjà l'utilisateur non-root « app ».
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChefOS.Back.dll"]
