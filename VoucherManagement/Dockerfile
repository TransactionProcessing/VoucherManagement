FROM stuartferguson/txnprocbase:latest AS base
RUN apt-get update
RUN apt-get install -y apt-utils
RUN apt-get install -y libgdiplus
RUN ln -s /usr/lib/libgdiplus.so /usr/lib/gdiplus.dll
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["VoucherManagement/NuGet.Config", "."]
COPY ["VoucherManagement/VoucherManagement.csproj", "VoucherManagement/"]
RUN dotnet restore "VoucherManagement/VoucherManagement.csproj"
COPY . .
WORKDIR "/src/VoucherManagement"
RUN dotnet build "VoucherManagement.csproj" -c Release -o /app/build

# Sort out certificate stuff here
RUN openssl x509 -inform DER -in /src/VoucherManagement/aspnetapp-root-cert.cer -out /src/VoucherManagement/aspnetapp-root-cert.crt
RUN cp /src/VoucherManagement/aspnetapp-root-cert.crt /usr/local/share/ca-certificates/
RUN update-ca-certificates

FROM build AS publish
RUN dotnet publish "VoucherManagement.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "VoucherManagement.dll"]