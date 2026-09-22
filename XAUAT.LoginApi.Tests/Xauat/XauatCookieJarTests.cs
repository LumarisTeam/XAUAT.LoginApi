using System.Net;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.Xauat;

public class XauatCookieJarTests
{
    private const string AuthHost = XauatConstants.AuthServerHost;
    private const string StudentHost = XauatConstants.StudentHost;

    private static HttpResponseMessage ResponseWithCookies(params string[] setCookieHeaders)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var header in setCookieHeaders)
        {
            response.Headers.TryAddWithoutValidation("Set-Cookie", header);
        }

        return response;
    }

    [Fact]
    public void CookieHeaderFor_ShouldIsolateCookiesByHost()
    {
        // 这是并发安全的关键：authserver 的 JSESSIONID 绝不能被发到 swjw，反之亦然。
        // 共享 CookieContainer 的实现会让并发登录互相串号。
        var jar = new XauatCookieJar();
        jar.Capture(AuthHost, ResponseWithCookies("JSESSIONID=auth-session; Path=/; HttpOnly"));
        jar.Capture(StudentHost, ResponseWithCookies("__pstsid__=edu-session; Path=/"));

        Assert.Equal("JSESSIONID=auth-session", jar.CookieHeaderFor(AuthHost));
        Assert.Equal("__pstsid__=edu-session", jar.CookieHeaderFor(StudentHost));
    }

    [Fact]
    public void CookieHeaderFor_ShouldStripCookieAttributes()
    {
        var jar = new XauatCookieJar();
        jar.Capture(StudentHost,
            ResponseWithCookies("__pstsid__=v; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=1200"));

        Assert.Equal("__pstsid__=v", jar.CookieHeaderFor(StudentHost));
    }

    [Fact]
    public void CookieHeaderFor_ShouldJoinMultipleCookiesInInsertionOrder()
    {
        var jar = new XauatCookieJar();
        jar.Capture(StudentHost, ResponseWithCookies("__pstsid__=a", "SESSION=b"));

        Assert.Equal("__pstsid__=a; SESSION=b", jar.CookieHeaderFor(StudentHost));
    }

    [Fact]
    public void Capture_ShouldReplaceSameNameAndKeepOriginalPosition()
    {
        var jar = new XauatCookieJar();
        jar.Capture(StudentHost, ResponseWithCookies("__pstsid__=old", "SESSION=b"));
        jar.Capture(StudentHost, ResponseWithCookies("__pstsid__=new"));

        Assert.Equal("__pstsid__=new; SESSION=b", jar.CookieHeaderFor(StudentHost));
    }

    [Fact]
    public void Capture_ShouldUnquoteValues()
    {
        var jar = new XauatCookieJar();
        jar.Capture(StudentHost, ResponseWithCookies("token=\"quoted\""));

        Assert.Equal("token=quoted", jar.CookieHeaderFor(StudentHost));
    }

    [Fact]
    public void Seed_ShouldAcceptFlaskStyleCookieString()
    {
        // 缓存的 SSO 票据就是 Flask 存的 "; " 拼接形态，必须能直接塞回罐子复用
        var jar = new XauatCookieJar();
        jar.Seed(AuthHost, "CASTGC=TGT-12345");

        Assert.Equal("CASTGC=TGT-12345", jar.CookieHeaderFor(AuthHost));
    }

    [Fact]
    public void Seed_ShouldIgnoreMalformedSegments()
    {
        var jar = new XauatCookieJar();
        jar.Seed(AuthHost, "CASTGC=TGT-12345; ; noequals; =novalue");

        Assert.Equal("CASTGC=TGT-12345", jar.CookieHeaderFor(AuthHost));
    }

    [Fact]
    public void AllAsString_ShouldJoinAcrossHostsInInsertionOrder()
    {
        // 对应 Flask 的 _cookies_to_string(self.session.cookies)：登录页返回 200 的成功分支
        // 会把整个会话的 cookie 一次性串出来
        var jar = new XauatCookieJar();
        jar.Capture(AuthHost, ResponseWithCookies("CASTGC=ticket"));
        jar.Capture(StudentHost, ResponseWithCookies("__pstsid__=edu"));

        Assert.Equal("CASTGC=ticket; __pstsid__=edu", jar.AllAsString());
    }

    [Fact]
    public void ReadResponseCookies_ShouldOnlyReadThatResponseNotTheWholeJar()
    {
        // loginFromSSO 取的是"某一跳种下的 cookie"（Flask 的 resp.cookies），
        // 不是累积后的会话 cookie。两者混用会让换票取错 cookie。
        var jar = new XauatCookieJar();
        jar.Capture(AuthHost, ResponseWithCookies("CASTGC=ticket"));
        var hop = ResponseWithCookies("__pstsid__=from-this-hop", "SESSION=also-this-hop");

        Assert.Equal("__pstsid__=from-this-hop; SESSION=also-this-hop",
            XauatCookieJar.ReadResponseCookies(hop));
    }

    [Fact]
    public void HasCookiesFor_ShouldReportPerHost()
    {
        var jar = new XauatCookieJar();
        jar.Capture(StudentHost, ResponseWithCookies("a=1"));

        Assert.True(jar.HasCookiesFor(StudentHost));
        Assert.False(jar.HasCookiesFor(AuthHost));
    }

    [Fact]
    public void CookieHeaderFor_ShouldReturnEmptyWhenNoCookies()
    {
        Assert.Equal("", new XauatCookieJar().CookieHeaderFor(StudentHost));
    }
}
