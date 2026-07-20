using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;
namespace AgentSupervisor.UnitTests;

public class BasicTests { [Fact] public void Layout_has_expected_folders() { var x = new LocalAppDataLayout("x"); Assert.EndsWith("bin", x.Bin); Assert.EndsWith("runtime", x.Runtime); } [Fact] public void Secret_round_trip() { var s = new SecretStore(); Assert.Equal("secret", s.Unprotect(s.Protect("secret"))); } }
