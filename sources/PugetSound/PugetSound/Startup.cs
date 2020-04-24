using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AspNet.Security.OAuth.Spotify;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using PugetSound.Auth;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Enums;

namespace PugetSound
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<RoomService>();

            services.AddControllersWithViews();

            services.AddSignalR();

            TokenRefreshService.Instance.SetAccessKeys(Configuration["SpotifyClientId"], Configuration["SpotifyClientSecret"]);

            services.AddAuthentication(o => o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
              .AddCookie(options =>
              {
                  options.Events = new CookieAuthenticationEvents
                  {
                      OnValidatePrincipal = async context =>
                      {
                          //check to see if user is authenticated first
                          if (context.Principal.Identity.IsAuthenticated)
                          {
                              try
                              {
                                  /*
                                   * big thanks to https://stackoverflow.com/questions/52175302/handling-expired-refresh-tokens-in-asp-net-core
                                   * see https://developer.spotify.com/documentation/general/guides/authorization-guide/#authorization-code-flow
                                   * could be useful to debug https://stackoverflow.com/questions/18924996/logging-request-response-messages-when-using-httpclient
                                   */

                                  //get the users tokens
                                  var tokens = context.Properties.GetTokens().ToList();
                                  var refreshToken = tokens.First(t => t.Name == "refresh_token");
                                  var accessToken = tokens.First(t => t.Name == "access_token");
                                  var exp = tokens.First(t => t.Name == "expires_at");
                                  var expires = DateTime.Parse(exp.Value);

                                  //check to see if the token has expired
                                  if (expires < DateTime.Now)
                                  {
                                      //token is expired, let's attempt to renew
                                      var tokenResponse = await TokenRefreshService.Instance.TryRefreshTokenAsync(refreshToken.Value);

                                      //set new token values
                                      if (string.IsNullOrWhiteSpace(tokenResponse.refresh_token)) refreshToken.Value = tokenResponse.refresh_token;
                                      accessToken.Value = tokenResponse.access_token;
                                      //set new expiration date
                                      var newExpires = DateTime.UtcNow + TimeSpan.FromSeconds(tokenResponse.expires_in);
                                      exp.Value = newExpires.ToString("o", CultureInfo.InvariantCulture);
                                      //set tokens in auth properties
                                      context.Properties.StoreTokens(tokens);
                                      //trigger context to renew cookie with new token values
                                      context.ShouldRenew = true;
                                  }

                                  // store latest tokens
                                  TokenRefreshService.Instance.StoreToken(context.Principal.Claims.GetSpotifyUsername(), refreshToken.Value);
                              }
                              catch (Exception e)
                              {
                                  Debug.WriteLine(e);
                                  context.RejectPrincipal();
                              }
                          }
                      }
                  };
              })
              .AddSpotify(options =>
              {
                  var scopes = Scope.AppRemoteControl
                               | Scope.UserReadPlaybackState
                               | Scope.UserModifyPlaybackState
                               | Scope.PlaylistModifyPrivate
                               | Scope.PlaylistReadPrivate
                               | Scope.PlaylistModifyPublic
                               | Scope.PlaylistReadCollaborative
                               | Scope.UserLibraryRead
                               | Scope.UserReadPrivate
                               | Scope.UserReadCurrentlyPlaying;
                  options.Scope.Add(scopes.GetStringAttribute(","));
                  options.ClientId = Configuration["SpotifyClientId"];
                  options.ClientSecret = Configuration["SpotifyClientSecret"];
                  options.CallbackPath = "/callback";
                  options.Events.OnRemoteFailure = context => Task.CompletedTask; // TODO handle rip
                  options.SaveTokens = true;
              });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();


            app.UseEndpoints(endpoints =>
            {
                //endpoints.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
                endpoints.MapControllerRoute(name: "default", pattern: "{action=Index}/{id?}", defaults: new { controller = "Home", action = "Index"});
                endpoints.MapHub<RoomHub>("/roomhub");
            });
        }
    }
}