# Build Instructions

## For Windows

To build the Technitium DNS Server Windows Setup, you need to install [Microsoft Visual Studio Community 2022 (VS2022)](https://visualstudio.microsoft.com/vs/) and [Inno Setup](https://jrsoftware.org/isinfo.php) on your computer. Once you have it installed, follow the steps below:

1. Open VS2022 and use the "Clone a repository" option to clone the [TechnitiumLibrary](https://github.com/TechnitiumSoftware/TechnitiumLibrary) project using the `https://github.com/TechnitiumSoftware/TechnitiumLibrary.git` URL. Once the repository is cloned and opened in VS2022, select the build mode to "Release" from the dropdown box in the toolbar and use the Build > Build Solution menu to build it.

2. Open VS2022 and use the "Clone a repository" option to clone the [DnsServer](https://github.com/TechnitiumSoftware/DnsServer) project using the `https://github.com/TechnitiumSoftware/DnsServer.git` URL in the same parent folder that you had cloned the TechnitiumLibrary repository in previous step. Once the repository is cloned and opened in VS2022, right click on the `DnsServerSystemTrayApp` project and click on the Publish menu to open the publish page. Click the Publish button on it to publish the project in `DnsServer\DnsServerWindowsSetup\publish` folder. Similarly, right click on the `DnsServerWindowsService` project and click on the Publish menu to open publish page and use the Publish button to publish the project in the same folder as that of the previous project.

3. Open the `DnsServer\DnsServerWindowsSetup\DnsServerSetup.iss` file in Inno Setup and click on the Build > Compile menu to generate a Windows setup in `DnsServerWindowsSetup\Release` folder that you can then use to install Technitium DNS Server on Windows.

## For Linux

### Install as a systemd service

Follow the instructions given below to build and install the DNS server from source. These instructions are written for Ubuntu and Raspberry Pi OS but, you can easily follow similar steps on your favorite distro.

1. Install prerequisites like curl and git.
```
sudo apt update
sudo apt install curl git -y
```

2. Follow the [install instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install?tabs=dotnet9&pivots=os-linux-ubuntu-2404) to be able to install ASP.NET Core SDK on your distro. Use the instructions given in the link to install the repository for other distros not shown in below examples:

- Ubuntu 24.04
```
sudo add-apt-repository ppa:dotnet/backports
sudo apt update
```

- Raspberry Pi OS
```
curl -sSL https://packages.microsoft.com/keys/microsoft.asc | sudo apt-key add -
sudo apt-add-repository https://packages.microsoft.com/debian/11/prod
sudo apt update
```

3. Install ASP.NET Core 9 SDK and `libmsquic` for DNS-over-QUIC support.
```
sudo apt install dotnet-sdk-9.0 libmsquic -y
```

4. Clone the source code for both [TechnitiumLibrary](https://github.com/TechnitiumSoftware/TechnitiumLibrary) and [DnsServer](https://github.com/TechnitiumSoftware/DnsServer) into the current folder.
```
git clone --depth 1 https://github.com/TechnitiumSoftware/TechnitiumLibrary.git TechnitiumLibrary
git clone --depth 1 https://github.com/TechnitiumSoftware/DnsServer.git DnsServer
```

5. Build the TechnitiumLibrary source.
```
dotnet build TechnitiumLibrary/TechnitiumLibrary.ByteTree/TechnitiumLibrary.ByteTree.csproj -c Release
dotnet build TechnitiumLibrary/TechnitiumLibrary.Net/TechnitiumLibrary.Net.csproj -c Release
dotnet build TechnitiumLibrary/TechnitiumLibrary.Security.OTP/TechnitiumLibrary.Security.OTP.csproj -c Release
```

6. Build the DnsServer source.
```
dotnet publish DnsServer/DnsServerApp/DnsServerApp.csproj -c Release
```

7. Install the DNS server as a systemd service.
```
sudo mkdir -p /opt/technitium/dns
sudo cp -r DnsServer/DnsServerApp/bin/Release/publish/* /opt/technitium/dns
sudo cp /opt/technitium/dns/systemd.service /etc/systemd/system/dns.service
sudo systemctl stop systemd-resolved
sudo systemctl disable systemd-resolved
sudo systemctl enable dns.service
sudo systemctl start dns.service
sudo rm /etc/resolv.conf
echo "nameserver 127.0.0.1" | sudo tee /etc/resolv.conf
```

8. Open the DNS server web console in a web browser using `http://<server-ip-address>:5380/` URL and set a login password to complete the installation.

### Build and run a Docker image

The Dockerfile uses a multi-stage build: the first stage compiles the source entirely inside a container (no .NET SDK required on the host), and the second stage produces the final runtime image.

**Prerequisites:** `git` and `docker` (with BuildKit support — Docker 23+ recommended).

1. Clone the DnsServer repository.
```
git clone --depth 1 https://github.com/TechnitiumSoftware/DnsServer.git DnsServer
```

2. Build the Docker image from the `DnsServer` directory.
```
cd DnsServer
docker build -t technitium/dns-server:latest .
```

Note! TechnitiumLibrary is fetched automatically during the Docker build. No separate clone or `dotnet` installation is needed on the host.

Note! BuildKit caches NuGet packages and apt packages between builds. Subsequent builds are significantly faster.

3. Run the image using `docker compose`. Edit `docker-compose.yml` to adjust the configuration before starting.
```
sudo systemctl stop systemd-resolved
sudo systemctl disable systemd-resolved
docker compose up -d
```

4. Open the DNS server web console in a web browser using `http://<server-ip-address>:5380/` URL and set a login password to complete the installation.
