﻿module Suave.Tests.Auth


open System
open System.Net
open System.Net.Http
open Fuchu
open Suave
open Suave.Logging
open Suave.Logging.Message
open Suave.Cookie
open Suave.State.CookieStateStore
open Suave.Operators
open Suave.Successful
open Suave.Filters
open Suave.RequestErrors
open Suave.Authentication
open Suave.Testing

type Assert with
  static member Null (msg : string, o : obj) =
    if o <> null then Tests.failtest msg
    else ()

  static member Contains (msg : string, fExpected : 'a -> bool, xs : seq<'a>) =
    if Seq.isEmpty xs then Tests.failtest "empty seq"
    match Seq.tryFind fExpected xs with
    | None -> Tests.failtest msg
    | Some v ->
      // printfn "found %A" v
      ()

let reqResp
  (methd : HttpMethod)
  (resource : string)
  (cookies : CookieContainer option)
  (fRequest : HttpRequestMessage -> HttpRequestMessage)
  fResult
  (ctx : SuaveTestCtx) =

  let event message =
    eventX message >> setSingleName "Suave.Tests"

  let logger =
    ctx.suaveConfig.logger

  logger.debug (
    event "{method} {resource}"
    >> setFieldValue "method" methd
    >> setFieldValue "resource" resource)

  let defaultTimeout = TimeSpan.FromSeconds 5.

  use handler = createHandler DecompressionMethods.None cookies
  use client = createClient handler
  use request = createRequest methd resource "" None (endpointUri ctx.suaveConfig) |> fRequest

  for h in request.Headers do
    logger.debug (event "{headerName}: {headerValue}"
                  >> setFieldValue "headerName" h.Key
                  >> setFieldValue "headerValue" (String.Join(", ", h.Value)))

  // use -> let!!!
  let result = request |> send client defaultTimeout ctx
  fResult result

let setConnectionKeepAlive (r : HttpRequestMessage) =
  r.Headers.ConnectionClose <- Nullable(false)
  r

/// Test a request by looking at the cookies alone.
let reqCookies cookies ctx methd resource fReq =
  reqResp methd resource (Some cookies)
           setConnectionKeepAlive
           fReq
           ctx

let cookies suaveConfig (container : CookieContainer) =
  container.GetCookies(endpointUri suaveConfig)

let interaction ctx fCtx = withContext fCtx ctx

let interact methd resource container ctx =
  let response = reqCookies container ctx methd resource id
  match response.Headers.TryGetValues("Set-Cookie") with
  | false, _ -> ()
  | true, values -> values |> Seq.iter (fun cookie -> container.SetCookies(endpointUri ctx.suaveConfig, cookie))
  response

let sessionState f =
  context( fun r ->
    match HttpContext.state r with
    | None ->  RequestErrors.BAD_REQUEST "damn it"
    | Some store -> f store )

[<Tests>]
let authTests cfg =
  let runWithConfig = runWith { cfg with logger = Targets.create Warn }
  testList "auth tests" [
    testCase "baseline, no auth cookie" <| fun _ ->
      let ctx = runWithConfig (OK "ACK")
      let cookies = ctx |> reqCookies' HttpMethod.GET "/"  None
      Assert.Null("should not have auth cookie", cookies.[SessionAuthCookie])

    testCase "can set cookie" <| fun _ ->
      let ctx = runWithConfig (authenticated Session false >=> OK "ACK")
      let cookies = ctx |> reqCookies' HttpMethod.GET "/"  None
      Assert.NotNull("should have auth cookie", cookies.[SessionAuthCookie])

    testCase "can set MaxAge cookie" <| fun _ ->
      let timespan = System.TimeSpan.FromDays(13.0)
      let maxAge = Cookie.CookieLife.MaxAge timespan
      let ctx = runWithConfig (authenticated maxAge false >=> OK "ACK")
      let cookies = ctx |> reqCookies' HttpMethod.GET "/"  None
      Assert.NotNull("should have auth cookie", cookies.[SessionAuthCookie])

    testCase "can access authenticated contents when authenticate, and not after deauthenticate" <| fun _ ->
      // given
      let ctx =
        runWithConfig (
          choose [
            path "/" >=> OK "root"
            path "/auth" >=> authenticated Session false >=> OK "authed"
            path "/protected"
              >=> authenticate Session false
                               (fun () ->
                                 Choice2Of2(FORBIDDEN "please authenticate"))
                               (fun _ -> Choice2Of2(BAD_REQUEST "did you fiddle with our cipher text?"))
                               (OK "You have reached the place of your dreams!")
            path "/deauth" >=> deauthenticate >=> OK "deauthed"
            NOT_FOUND "arghhh"
            ])

      // mutability bonanza here:
      let container = CookieContainer()
      let interact methd resource = interact methd resource container ctx
      let cookies = cookies ctx.suaveConfig container

      // when
      interaction ctx <| fun _ ->
        use res = interact HttpMethod.GET "/"
        Assert.Equal("should allow root request", "root", contentString res)

        match cookies.[SessionAuthCookie] with
        | null -> ()
        | cookie -> Tests.failtestf "should not have auth cookie, but was %A" cookie

        use res' = interact HttpMethod.GET "/protected"
        Assert.Equal("should not have access to protected", "please authenticate", contentString res')
        Assert.Equal("code 403 FORBIDDEN", HttpStatusCode.Forbidden, statusCode res')

        use res'' = interact HttpMethod.GET "/auth"
        Assert.Contains("after authentication", (fun (str : string) -> str.Contains("auth=")),
                                                res''.Headers.GetValues "Set-Cookie")
        Assert.Equal("after authentication", "authed", contentString res'')

        use res''' = interact HttpMethod.GET "/protected"
        Assert.Equal("should have access to protected", "You have reached the place of your dreams!", contentString res''')
        Assert.Equal("code 200 OK", HttpStatusCode.OK, statusCode res''')

        use res'''' = interact HttpMethod.GET "/deauth"
        Assert.Equal("should have logged out now", "deauthed", contentString res'''')

        use res''''' = interact HttpMethod.GET "/protected"
        Assert.Equal("should not have access to protected after logout","please authenticate", contentString res''''')

    testCase "test session is maintained across requests" <| fun _ ->
      // given
      let ctx =
        runWithConfig (
          statefulForSession
          >=> sessionState (fun store ->
              match store.get "counter" with
              | Some y ->
                store.set "counter" (y + 1)
                >=> OK ((y + 1).ToString())
              | None ->
                store.set "counter" 0
                >=> OK "0"))

      let container = CookieContainer()
      let interact methd resource = interact methd resource container ctx

      interaction ctx  (fun _ ->
        use res = interact HttpMethod.GET "/"
        Assert.Equal("should return number zero", "0", contentString res)

        use res' = interact HttpMethod.GET "/"
        Assert.Equal("should return number one", "1", contentString res')

        use res'' = interact HttpMethod.GET "/"
        Assert.Equal("should return number two", "2", contentString res''))

    testCase "set more than one variable in the session" <| fun _ ->
      // given
      let ctx =
        runWithConfig (
          statefulForSession
          >=> choose [
            path "/a"     >=> sessionState (fun state -> state.set "a" "a" >=> OK "a" )
            path "/b"     >=> sessionState (fun state -> state.set "b" "b" >=> OK "b" )
            path "/get_a" >=> sessionState (fun state -> match state.get "a" with Some a -> OK a | None -> RequestErrors.BAD_REQUEST "fail")
            path "/get_b" >=> sessionState (fun state -> match state.get "b" with Some a -> OK a | None -> RequestErrors.BAD_REQUEST "fail" )
            ])

      let container = CookieContainer()
      let interact methd resource = interact methd resource container ctx

      interaction ctx  (fun _ ->
        use res = interact HttpMethod.GET "/a"
        Assert.Equal("should return a", "a", contentString res)

        use res' = interact HttpMethod.GET "/b"
        Assert.Equal("should return b", "b", contentString res')

        use res'' = interact HttpMethod.GET "/get_a"
        Assert.Equal("should return a", "a", contentString res'')

        use res''' = interact HttpMethod.GET "/get_b"
        Assert.Equal("should return b", "b", contentString res'''))

    testCase "set two session values on the same request" <| fun _ ->
      // given
      let ctx =
        runWithConfig (
          statefulForSession >=> choose [
            path "/ab"     >=> sessionState (fun state -> state.set "a" "a" >=> sessionState ( fun state' -> state'.set "b" "b" >=> OK "a" ))
            path "/get_a" >=> sessionState (fun state -> match state.get "a" with Some a -> OK a | None -> RequestErrors.BAD_REQUEST "fail")
            path "/get_b" >=> sessionState (fun state -> match state.get "b" with Some a -> OK a | None -> RequestErrors.BAD_REQUEST "fail" )
            ])

      let container = CookieContainer()
      let interact methd resource = interact methd resource container ctx

      interaction ctx  (fun _ ->
        use res = interact HttpMethod.GET "/ab"
        Assert.Equal("should return a", "a", contentString res)

        use res''' = interact HttpMethod.GET "/get_b"
        Assert.Equal("should return b", "b", contentString res''')

        use res'' = interact HttpMethod.GET "/get_a"
        Assert.Equal("should return a", "a", contentString res''))
    ]
