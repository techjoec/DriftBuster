job "web" {
  datacenters = ["dc1"]
  type = "service"
  group "app" {
    count = 2
    task "server" {
      driver = "docker"
    }
  }
}
